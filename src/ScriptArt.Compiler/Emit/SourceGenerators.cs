using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ScriptArt.Compiler.Emit;

/// <summary>A source generator assembly the user approved, with the SHA-256 of the file they approved.</summary>
public sealed record SourceGeneratorAssembly(string Path, string Sha256);

/// <summary>
/// The source generators verification runs, such as Godot's (D-050), and the MSBuild properties they read, as a build passes them
/// (<c>build_property.Name</c>).
/// </summary>
public sealed record SourceGeneratorSet(IReadOnlyList<SourceGeneratorAssembly> Assemblies, IReadOnlyDictionary<string, string> BuildProperties);

/// <summary>
/// Loads source generators from approved assemblies. An assembly is read into memory, its hash checked against the approved one, and
/// loaded from those bytes, so a file changed after approval is never run. Loaded assemblies stay loaded, once per hash.
/// </summary>
internal static class SourceGeneratorLoader
{
    private static readonly ConcurrentDictionary<string, ImmutableArray<ISourceGenerator>> Loaded = new(StringComparer.Ordinal);

    /// <summary>The C# generators in <paramref name="assembly"/>; false, with the reason, when the file is missing, changed, or not loadable.</summary>
    public static bool TryLoad(SourceGeneratorAssembly assembly, out ImmutableArray<ISourceGenerator> generators, [NotNullWhen(false)] out string? problem)
    {
        generators = [];
        if (Loaded.TryGetValue(assembly.Sha256, out generators))
        {
            problem = null;
            return true;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(assembly.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            problem = $"The source generator {assembly.Path} could not be read: {exception.Message}";
            return false;
        }

        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), assembly.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            problem = $"The source generator {assembly.Path} changed after it was approved, so it was not run. Approve it again to run it.";
            return false;
        }

        try
        {
            Assembly loaded = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(bytes));
            generators = Loaded.GetOrAdd(assembly.Sha256, _ => [.. Types(loaded).Select(Create).OfType<ISourceGenerator>()]);
            problem = null;
            return true;
        }
        catch (Exception exception) when (exception is BadImageFormatException or FileLoadException or TargetInvocationException or MissingMethodException)
        {
            problem = $"The source generator {assembly.Path} could not be loaded: {exception.Message}";
            return false;
        }
    }

    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }

    // A [Generator] class for C#, created like the compiler does: incremental generators are wrapped.
    private static ISourceGenerator? Create(Type type)
    {
        if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null
            || type.GetCustomAttribute<GeneratorAttribute>() is not { } attribute || !attribute.Languages.Contains(LanguageNames.CSharp))
        {
            return null;
        }

        return Activator.CreateInstance(type) switch
        {
            IIncrementalGenerator incremental => incremental.AsSourceGenerator(),
            ISourceGenerator generator => generator,
            _ => null,
        };
    }
}

/// <summary>Gives generators the MSBuild properties a build makes visible to them, and nothing per file.</summary>
internal sealed class BuildPropertyOptionsProvider(IReadOnlyDictionary<string, string> properties) : AnalyzerConfigOptionsProvider
{
    private readonly DictionaryOptions global = new(properties.ToDictionary(property => "build_property." + property.Key, property => property.Value, StringComparer.OrdinalIgnoreCase));

    public override AnalyzerConfigOptions GlobalOptions => global;

    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => DictionaryOptions.Empty;

    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => DictionaryOptions.Empty;

    private sealed class DictionaryOptions(Dictionary<string, string> values) : AnalyzerConfigOptions
    {
        public static readonly DictionaryOptions Empty = new([]);

        public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value) => values.TryGetValue(key, out value);
    }
}

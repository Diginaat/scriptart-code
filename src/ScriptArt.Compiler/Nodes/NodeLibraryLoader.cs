using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;

namespace ScriptArt.Compiler.Nodes;

/// <summary>The nodes a node library assembly provides, and what went wrong loading it.</summary>
/// <param name="Sha256">The hash of the assembly file that was loaded, in lowercase hex.</param>
public sealed record NodeLibraryContents(string AssemblyPath, string Sha256, IReadOnlyList<NodeDefinition> Definitions, IReadOnlyList<string> Problems);

/// <summary>
/// Loads node library assemblies: assemblies that reference <c>ScriptArt.NodeSdk</c> and implement <see cref="INodeProvider"/>.
/// </summary>
/// <remarks>
/// Loading a library runs its code inside the IDE with the user's permissions. Callers load only libraries the user chose
/// explicitly (D-038); nothing here decides whether a library is trusted. Each library gets its own load context, which resolves
/// its dependencies from its folder and shares the SDK and graph model with the IDE so their types match.
/// </remarks>
public static class NodeLibraryLoader
{
    // A library is loaded once per file content; loading the same file again returns the same definitions.
    private static readonly ConcurrentDictionary<(string Path, string Sha256), NodeLibraryContents> Loaded = new();

    /// <summary>
    /// Loads the library at <paramref name="assemblyPath"/> and creates its nodes. Never throws for a faulty library. With
    /// <paramref name="expectedSha256"/>, a file whose content has a different hash is not loaded. The bytes that are hashed are the
    /// bytes that are loaded, so the file cannot change in between.
    /// </summary>
    public static NodeLibraryContents Load(string assemblyPath, string? expectedSha256 = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        string path = Path.GetFullPath(assemblyPath);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(path, "", [], [$"{Path.GetFileName(path)}: {exception.Message}"]);
        }

        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (expectedSha256 is not null && !string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new(path, hash, [], [$"{Path.GetFileName(path)} changed since it was approved, so it was not loaded. Approve it again if you trust the new version."]);
        }

        return Loaded.GetOrAdd((path, hash), key => LoadNew(key.Path, bytes, key.Sha256));
    }

    /// <summary>The SHA-256 hash of a file, in lowercase hex.</summary>
    public static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static NodeLibraryContents LoadNew(string path, byte[] bytes, string hash)
    {
        string name = Path.GetFileName(path);
        List<string> problems = [];
        List<NodeDefinition> definitions = [];
        Assembly assembly;
        try
        {
            using var stream = new MemoryStream(bytes);
            assembly = new LibraryLoadContext(path).LoadFromStream(stream);
        }
        catch (Exception exception) when (exception is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return new(path, hash, [], [$"{name} is not a .NET assembly that can be loaded: {exception.Message}"]);
        }

        Type[] providers;
        try
        {
            providers = [.. assembly.GetExportedTypes().Where(type => type is { IsClass: true, IsAbstract: false } && typeof(INodeProvider).IsAssignableFrom(type))];
        }
        catch (Exception exception) when (exception is ReflectionTypeLoadException or FileNotFoundException or FileLoadException or TypeLoadException)
        {
            return new(path, hash, [], [$"{name}: its types could not be loaded: {exception.Message}"]);
        }

        if (providers.Length == 0)
        {
            problems.Add($"{name} has no public class implementing {nameof(INodeProvider)}, so it adds no nodes.");
        }

        foreach (Type provider in providers)
        {
            try
            {
                var instance = (INodeProvider)Activator.CreateInstance(provider)!;
                definitions.AddRange(instance.CreateNodes());
            }
#pragma warning disable CA1031 // A library is third-party code: any exception it throws is reported, never allowed to stop the IDE.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                Exception cause = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
                problems.Add($"{name}: {provider.FullName} failed to create its nodes: {cause.Message}");
            }
        }

        return new(path, hash, definitions, problems);
    }

    private sealed class LibraryLoadContext(string assemblyPath) : AssemblyLoadContext($"NodeLibrary:{Path.GetFileName(assemblyPath)}")
    {
        private readonly AssemblyDependencyResolver resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // The SDK and graph model, and the framework, come from the IDE so a library's nodes are the IDE's types.
            if (Default.Assemblies.Any(loaded => AssemblyName.ReferenceMatchesDefinition(assemblyName, loaded.GetName())))
            {
                return null;
            }

            return resolver.ResolveAssemblyToPath(assemblyName) is { } path ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) =>
            resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}

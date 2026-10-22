using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ScriptArt.Compiler.Emit;

/// <summary>
/// A .NET startup hook (<c>DOTNET_STARTUP_HOOKS</c>) that holds a program before <c>Main</c> until a debugger attaches, so the IDE can
/// start the program itself, with its input and output connected, and still stop at breakpoints on the first statements.
/// </summary>
/// <remarks>
/// The hook is part of the IDE, not of the user's project: it is compiled once into a temporary folder and only named by the
/// environment of a program started for debugging. Its source is fixed, not generated from graphs.
/// </remarks>
public static class DebugStartupHook
{
    /// <summary>Names a file the hook creates once the runtime is up and waiting, which tells the IDE it can attach.</summary>
    public const string ReadyFileVariable = "VSIDE_DEBUG_READY_FILE";

    /// <summary>How long the hook waits, in seconds; without a debugger by then the program runs on.</summary>
    public const string TimeoutVariable = "VSIDE_DEBUG_WAIT_SECONDS";

    private const string AssemblyName = "ScriptArt.DebugStartupHook";

    // A startup hook is a type named StartupHook, outside any namespace, with a static Initialize method. It clears its own
    // variables first so processes the program starts do not wait for a debugger too.
    private const string Source = """
        internal static class StartupHook
        {
            public static void Initialize()
            {
                string? readyFile = System.Environment.GetEnvironmentVariable("VSIDE_DEBUG_READY_FILE");
                string? timeout = System.Environment.GetEnvironmentVariable("VSIDE_DEBUG_WAIT_SECONDS");
                System.Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", null);
                System.Environment.SetEnvironmentVariable("VSIDE_DEBUG_READY_FILE", null);
                System.Environment.SetEnvironmentVariable("VSIDE_DEBUG_WAIT_SECONDS", null);
                if (string.IsNullOrEmpty(readyFile))
                {
                    return;
                }

                System.IO.File.WriteAllText(readyFile, "");
                double seconds = double.TryParse(timeout, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) ? value : 30;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (!System.Diagnostics.Debugger.IsAttached && watch.Elapsed.TotalSeconds < seconds)
                {
                    System.Threading.Thread.Sleep(10);
                }
            }
        }
        """;

    /// <summary>
    /// The hook assembly in <paramref name="directory"/>, compiled there first when missing. The file name includes a hash of the
    /// source, so a changed hook never reuses an old assembly.
    /// </summary>
    public static string EnsureBuilt(string directory)
    {
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Source)))[..12];
        string path = Path.Combine(directory, $"{AssemblyName}.{hash}.dll");
        if (File.Exists(path))
        {
            return path;
        }

        CSharpCompilation compilation = CSharpCompilation.Create(
            AssemblyName,
            [CSharpSyntaxTree.ParseText(Source, new CSharpParseOptions(LanguageVersion.Latest))],
            RoslynVerifier.RuntimeReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        Directory.CreateDirectory(directory);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (FileStream stream = File.Create(temporary))
        {
            Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit(stream);
            if (!result.Success)
            {
                throw new InvalidOperationException("The debugging startup hook does not compile: " + string.Join("; ", result.Diagnostics));
            }
        }

        try
        {
            File.Move(temporary, path);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another IDE instance built the same hook meanwhile; both copies are identical.
            File.Delete(temporary);
        }

        return path;
    }
}

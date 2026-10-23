namespace ScriptArt.Compiler.Nodes;

/// <summary>
/// The entry point of a node library assembly. The IDE creates every public, non-abstract class implementing this interface with a
/// public parameterless constructor, once per loaded library, and registers the nodes it returns.
/// </summary>
/// <remarks>
/// Type IDs must be unique across every library; prefix them with the library's name, for example <c>acme.text.reverse</c>.
/// Nodes lower to IR, so the C# they generate must compile with the references of the projects that use them.
/// </remarks>
public interface INodeProvider
{
    /// <summary>The node definitions of the library.</summary>
    IEnumerable<NodeDefinition> CreateNodes();
}

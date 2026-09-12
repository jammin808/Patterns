using System.Reflection;
using System.Reflection.Emit;
using Patterns.Core.Rendering;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The render core — the engine, the patterns, the effects, the particles, the lower thirds, the
/// media and NDI seams — is pure drawing over a snapshot. Break music, the assistant and the
/// network belong to the desk (the App fetches; Core parses), and this reads the compiled code to
/// make sure the seam stays where it is: no renderer may reach a Spotify or assistant type, a
/// socket or an HTTP client. A forecast reaches the weather chip as an immutable report on the
/// snapshot, which is the one crossing the chip needs.
/// </summary>
public class RenderCoreBoundaryTests
{
    private static readonly string[] RenderNamespaces =
    {
        "Patterns.Core.Rendering", "Patterns.Core.Patterns", "Patterns.Core.Effects", "Patterns.Core.Particles",
        "Patterns.Core.LowerThirds", "Patterns.Core.Media", "Patterns.Core.Ndi", "Patterns.Core.Audio",
    };

    private static bool Forbidden(Type t)
    {
        var ns = t.Namespace ?? "";
        if (ns.StartsWith("System.Net", StringComparison.Ordinal)) return true;
        if (ns != "Patterns.Core.Services") return false;
        var name = t.Name;
        return name.StartsWith("Spotify", StringComparison.Ordinal)
               || name.StartsWith("Assistant", StringComparison.Ordinal)
               || name.StartsWith("RemoteAdmin", StringComparison.Ordinal)
               || name.StartsWith("Management", StringComparison.Ordinal);
    }

    [Fact]
    public void TheRenderCoreNeverReachesTheIntegrationsOrTheNetwork()
    {
        var core = typeof(PatternEngine).Assembly;
        var offences = new List<string>();
        foreach (var type in core.GetTypes())
        {
            var ns = type.Namespace ?? "";
            if (!RenderNamespaces.Any(n => ns == n || ns.StartsWith(n + ".", StringComparison.Ordinal))) continue;
            foreach (var referenced in ReferencedTypes(type))
            {
                if (Forbidden(referenced)) offences.Add($"{type.FullName} → {referenced.FullName}");
            }
        }
        Assert.True(offences.Count == 0, "The render core reaches outside its seam:\n" + string.Join("\n", offences.Distinct()));
    }

    // ---- the reader: every type a type's signatures and method bodies name ----------------

    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var f in type.GetFields(all)) yield return f.FieldType;
        foreach (var p in type.GetProperties(all)) yield return p.PropertyType;
        foreach (var m in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
        {
            foreach (var param in m.GetParameters()) yield return param.ParameterType;
            if (m is MethodInfo mi) yield return mi.ReturnType;
            foreach (var t in BodyReferences(m)) yield return t;
        }
    }

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value, o => o);

    /// <summary>The member and type tokens in a method's IL, resolved: what the body calls, reads, writes or creates.</summary>
    private static IEnumerable<Type> BodyReferences(MethodBase method)
    {
        byte[]? il;
        try { il = method.GetMethodBody()?.GetILAsByteArray(); }
        catch { yield break; }
        if (il is null) yield break;
        var module = method.Module;
        var typeArgs = method.DeclaringType is { IsGenericType: true } dt ? dt.GetGenericArguments() : null;
        var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var i = 0;
        while (i < il.Length)
        {
            short value = il[i++];
            if (value == 0xFE) value = (short)(0xFE00 | il[i++]);
            if (!OpCodesByValue.TryGetValue(value, out var op)) yield break;
            Type? found = null;
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: i += 1; break;
                case OperandType.InlineVar: i += 2; break;
                case OperandType.InlineBrTarget:
                case OperandType.InlineI:
                case OperandType.ShortInlineR:
                case OperandType.InlineString:
                case OperandType.InlineSig: i += 4; break;
                case OperandType.InlineI8:
                case OperandType.InlineR: i += 8; break;
                case OperandType.InlineSwitch:
                    var n = BitConverter.ToInt32(il, i);
                    i += 4 + 4 * n;
                    break;
                case OperandType.InlineType:
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineTok:
                    var token = BitConverter.ToInt32(il, i);
                    i += 4;
                    try
                    {
                        found = module.ResolveMember(token, typeArgs, methodArgs) switch
                        {
                            Type t => t,
                            FieldInfo f => f.DeclaringType,
                            MethodBase mb => mb.DeclaringType,
                            _ => null,
                        };
                    }
                    catch
                    {
                        found = null; // a token this reader cannot resolve is not a type the body names
                    }
                    break;
                default: i += 4; break;
            }
            if (found is not null) yield return found;
        }
    }
}

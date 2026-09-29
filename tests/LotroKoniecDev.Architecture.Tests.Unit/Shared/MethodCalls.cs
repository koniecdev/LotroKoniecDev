using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace LotroKoniecDev.Architecture.Tests.Unit.Shared;

/// <summary>
/// Reads which methods a method body calls, straight from its IL.
/// </summary>
/// <remarks>
/// NetArchTest can say that a type depends on another type. It cannot say which method a call sits in,
/// and a rule about a pair of calls needs exactly that. Every method token is resolved through the
/// module that owns the body, so the answer is the method the call really binds to, not a name that
/// looks like it. Every operand size follows the operand types of ECMA-335, and that table is what keeps
/// the walk in step with the IL. The throws are only a backstop: a token that does not resolve, an
/// unknown opcode or a walk past the end of the body fails the rule loudly and names the body.
/// </remarks>
internal static class MethodCalls
{
    private const byte TwoByteOpCodePrefix = 0xFE;

    private const BindingFlags DeclaredMembers =
        BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => opCode.Value);

    /// <summary>
    /// Every method body in <paramref name="assembly"/>, the compiler-generated ones included. An async
    /// method's code runs in the <c>MoveNext</c> of its state machine, and a lambda's code in a method of
    /// its own, so a rule about calls has to read those bodies too.
    /// </summary>
    internal static IReadOnlyList<MethodBase> BodiesIn(Assembly assembly) =>
        assembly.GetTypes()
            .SelectMany(type => type.GetMethods(DeclaredMembers)
                .Cast<MethodBase>()
                .Concat(type.GetConstructors(DeclaredMembers)))
            .Where(method => method.GetMethodBody() is not null)
            .ToList();

    /// <summary>
    /// A call to any instantiation of a generic method counts as a call to that method. Taking a delegate
    /// to it (<c>ldftn</c>) counts too, because every operand that names a method is read.
    /// </summary>
    internal static bool CallsAnyOf(this MethodBase body, IReadOnlyCollection<MethodInfo> targets) =>
        CalledBy(body).Any(called => targets.Any(called.HasSameMetadataDefinitionAs));

    /// <summary>
    /// The <c>MoveNext</c> of an async or iterator state machine is named after the method that owns it.
    /// A lambda or a local function keeps its compiler name, which still shows the enclosing method in
    /// angle brackets, for example <c>&lt;Handle&gt;b__5_0</c>.
    /// </summary>
    internal static string SourceNameOf(MethodBase body)
    {
        Type declaringType = body.DeclaringType!;

        MethodInfo? stateMachineOwner = declaringType.DeclaringType?
            .GetMethods(DeclaredMembers)
            .FirstOrDefault(method => method.GetCustomAttribute<StateMachineAttribute>()?.StateMachineType == declaringType);

        return stateMachineOwner is null ? MetadataNameOf(body) : MetadataNameOf(stateMachineOwner);
    }

    private static List<MethodBase> CalledBy(MethodBase body)
    {
        try
        {
            return WalkCalls(body);
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            // A missing dependency after a package bump would otherwise fail the rule with no hint of where.
            // SourceNameOf reads attributes, which can fail for the same reason, so the message uses the raw name.
            throw new InvalidOperationException($"The IL walk of {MetadataNameOf(body)} failed.", exception);
        }
    }

    private static List<MethodBase> WalkCalls(MethodBase body)
    {
        byte[] il = body.GetMethodBody()?.GetILAsByteArray() ?? [];

        // ResolveMethod needs the body's own generic parameters to bind a token that uses them.
        Type[] typeArguments = body.DeclaringType is { IsGenericType: true } genericType
            ? genericType.GetGenericArguments()
            : Type.EmptyTypes;
        Type[] methodArguments = body.IsGenericMethod ? body.GetGenericArguments() : Type.EmptyTypes;

        List<MethodBase> called = [];
        int offset = 0;
        while (offset < il.Length)
        {
            OpCode opCode = OpCodeAt(il, offset);
            offset += opCode.Size;

            if (opCode.OperandType is OperandType.InlineMethod)
            {
                int token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset));
                called.Add(body.Module.ResolveMethod(token, typeArguments, methodArguments)
                    ?? throw new InvalidOperationException($"Method token 0x{token:X8} in {MetadataNameOf(body)} did not resolve."));
            }

            offset += OperandSize(opCode, il, offset);
        }

        if (offset != il.Length)
        {
            throw new InvalidOperationException($"The IL walk of {MetadataNameOf(body)} ran past the end of the body.");
        }

        return called;
    }

    private static string MetadataNameOf(MethodBase method) => $"{method.DeclaringType?.FullName}.{method.Name}";

    private static OpCode OpCodeAt(byte[] il, int offset)
    {
        short value = il[offset] is TwoByteOpCodePrefix
            ? unchecked((short)((TwoByteOpCodePrefix << 8) | il[offset + 1]))
            : il[offset];

        return OpCodesByValue[value];
    }

    private static int OperandSize(OpCode opCode, byte[] il, int operandOffset) =>
        opCode.OperandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod
                or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType
                or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => 4 + 4 * BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(operandOffset)),
            _ => throw new InvalidOperationException($"The IL walk does not know the operand type {opCode.OperandType}."),
        };
}

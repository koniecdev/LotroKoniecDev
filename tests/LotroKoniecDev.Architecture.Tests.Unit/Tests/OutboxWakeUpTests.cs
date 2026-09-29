using System.Reflection;
using LotroKoniecDev.Architecture.Tests.Unit.Shared;

namespace LotroKoniecDev.Architecture.Tests.Unit.Tests;

/// <summary>
/// Every method that calls <c>OutboxWriter.Enqueue</c> also calls <c>NotifyEnqueuedCommitted</c>. The
/// contract is on <c>OutboxWriter</c> and in ADR-0038 decision 6. This test exists because a forgotten
/// wake-up logs nothing: the mail still goes out, but hours late, and no alert fires (#781).
/// </summary>
/// <remarks>
/// The rule reads IL, not source. IL says which method a call really binds to, so an <c>Enqueue</c> on
/// some other type never matches, and a Razor page's code-behind is one more class like any other. It
/// also keeps this suite pure reflection: a source scan would have to find the repo on disk.
/// The unit is one method body as <see cref="MethodCalls.BodiesIn"/> reads it, so a lambda or a local
/// function counts on its own.
/// </remarks>
public sealed class OutboxWakeUpTests
{
    private const string OutboxWriterTypeName = $"{Namespaces.AuthSystemApi}.Outbox.OutboxWriter";

    private const BindingFlags DeclaredMethods =
        BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public void OutboxWriterEnqueue_EveryMethodThatCallsIt_AlsoCallsNotifyEnqueuedCommitted()
    {
        Type outboxWriter = ProductionAssemblies.AuthSystemApi.GetType(OutboxWriterTypeName)
            .ShouldNotBeNull($"{OutboxWriterTypeName} was moved or renamed — point this rule at the new name");
        List<MethodInfo> enqueue = MethodsNamed(outboxWriter, "Enqueue");
        enqueue.ShouldNotBeEmpty("OutboxWriter.Enqueue was renamed — point this rule at the new name");
        List<MethodInfo> notify = MethodsNamed(outboxWriter, "NotifyEnqueuedCommitted");
        notify.ShouldNotBeEmpty("OutboxWriter.NotifyEnqueuedCommitted was renamed — point this rule at the new name");

        List<MethodBase> enqueuers = CallersOf(enqueue, ProductionAssemblies.All);
        enqueuers.ShouldNotBeEmpty("no call to OutboxWriter.Enqueue was found — the rule would pass vacuously");

        List<string> violations = MissingTheWakeUp(enqueuers, notify);

        violations.ShouldBeEmpty(
            $"Enqueue only adds the row to the unit of work. The same method must call NotifyEnqueuedCommitted after the commit, or the mail waits for the relay's 6 h sweep. A lambda or a local function is a method of its own, so the call goes inside it, after the commit:{ViolationReport.Format(violations)}");
    }

    /// <summary>
    /// Proves the rule can still say "no". Every real writer pairs the two calls, so the test above cannot
    /// show that a body without the wake-up gets reported. It would stay green if the wake-up check
    /// always answered yes.
    /// </summary>
    [Fact]
    public void WakeUpRule_AgainstAnAsyncMethodThatForgetsTheWakeUp_ReportsOnlyThatMethod()
    {
        List<MethodInfo> enqueue = MethodsNamed(typeof(FixtureOutboxWriter), nameof(FixtureOutboxWriter.Enqueue));
        List<MethodInfo> notify = MethodsNamed(typeof(FixtureOutboxWriter), nameof(FixtureOutboxWriter.NotifyEnqueuedCommitted));

        List<string> violations = MissingTheWakeUp(CallersOf(enqueue, [typeof(FixtureSlices).Assembly]), notify);

        violations.ShouldBe([$"{typeof(FixtureSlices).FullName}.{nameof(FixtureSlices.ForgetsTheWakeUpAsync)}"]);
    }

    /// <summary>
    /// Every overload counts, so a second <c>Enqueue</c> can never slip past the rule unseen.
    /// </summary>
    private static List<MethodInfo> MethodsNamed(Type type, string name) =>
        type.GetMethods(DeclaredMethods)
            .Where(method => method.Name == name)
            .ToList();

    private static List<MethodBase> CallersOf(IReadOnlyCollection<MethodInfo> targets, IEnumerable<Assembly> assemblies) =>
        assemblies
            .SelectMany(MethodCalls.BodiesIn)
            .Where(body => body.CallsAnyOf(targets))
            .ToList();

    private static List<string> MissingTheWakeUp(IEnumerable<MethodBase> enqueuers, IReadOnlyCollection<MethodInfo> notify) =>
        enqueuers
            .Where(body => !body.CallsAnyOf(notify))
            .Select(MethodCalls.SourceNameOf)
            .ToList();

    private sealed class FixtureOutboxWriter
    {
        private readonly List<object> _unitOfWork = [];

        public void Enqueue<TMessage>(TMessage message) where TMessage : class
        {
            _unitOfWork.Add(message);
        }

        public void NotifyEnqueuedCommitted()
        {
        }
    }

    /// <summary>
    /// Both methods are async like every real writer, so the calls sit in a state machine's
    /// <c>MoveNext</c>. <c>Task.Yield</c> stands in for the commit.
    /// </summary>
    private sealed class FixtureSlices
    {
        private readonly FixtureOutboxWriter _outboxWriter = new();

        public async Task ForgetsTheWakeUpAsync()
        {
            _outboxWriter.Enqueue("fixture message");
            await Task.Yield();
        }

        public async Task WakesUpAfterTheCommitAsync()
        {
            _outboxWriter.Enqueue("fixture message");
            await Task.Yield();
            _outboxWriter.NotifyEnqueuedCommitted();
        }
    }
}

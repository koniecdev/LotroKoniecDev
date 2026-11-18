using System.Security.AccessControl;
using System.Security.Principal;
using LotroKoniecDev.DesktopApp;
using LotroKoniecDev.DesktopApp.Presentation;

namespace LotroKoniecDev.Tests.Infrastructure.Tests;

/// <summary>
/// The Windows answers behind the single-copy rule (#1464) and the uninstaller's check (spec 0016).
/// Windows can refuse a name another kind of object holds, and the app must start anyway.
/// </summary>
public sealed class DesktopAppRunningMutexTests
{
    [Fact]
    public void Open_AFreeName_CreatesAMutexOthersCanFind()
    {
        // Arrange
        string name = $"LotroPoPolsku_Test_{Guid.NewGuid():N}";
        WindowsRunningMutexOpener sut = new();

        // Act
        RunningMutexAttempt attempt = sut.Open(name);

        // Assert: the uninstaller looks the name up, as TryOpenExisting does.
        using IDisposable? handle = attempt.Handle;
        attempt.State.ShouldBe(RunningMutexState.Created);
        handle.ShouldNotBeNull();
        Mutex.TryOpenExisting(name, out Mutex? found).ShouldBeTrue();
        found?.Dispose();
    }

    [Fact]
    public void Open_ANameAnotherCopyHolds_SaysItAlreadyExists()
    {
        // Arrange
        string name = $"LotroPoPolsku_Test_{Guid.NewGuid():N}";
        using Mutex firstCopy = new(initiallyOwned: false, name);
        WindowsRunningMutexOpener sut = new();

        // Act
        RunningMutexAttempt attempt = sut.Open(name);

        // Assert
        using IDisposable? handle = attempt.Handle;
        attempt.State.ShouldBe(RunningMutexState.AlreadyExists);
        handle.ShouldNotBeNull();
    }

    [Fact]
    public void Open_ANameWindowsRefuses_IsRefusedInsteadOfThrowing()
    {
        // Arrange: a semaphore holds the name, so Windows refuses a mutex under it.
        string name = $"LotroPoPolsku_Test_{Guid.NewGuid():N}";
        using Semaphore holder = new(initialCount: 1, maximumCount: 1, name);
        WindowsRunningMutexOpener sut = new();

        // Act
        RunningMutexAttempt attempt = sut.Open(name);

        // Assert
        attempt.State.ShouldBe(RunningMutexState.Refused);
        attempt.Handle.ShouldBeNull();
    }

    [Fact]
    public void Open_ANameWhoseAccessListRefusesUs_IsRefusedInsteadOfThrowing()
    {
        // Arrange: another program's mutex under the name, with an access list that lets nobody open it.
        string name = $"LotroPoPolsku_Test_{Guid.NewGuid():N}";
        MutexSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new MutexAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, domainSid: null),
            MutexRights.FullControl,
            AccessControlType.Deny));
        using Mutex holder = MutexAcl.Create(initiallyOwned: false, name, out _, security);
        WindowsRunningMutexOpener sut = new();

        // Act
        RunningMutexAttempt attempt = sut.Open(name);

        // Assert: a throw here would end the app before its logger exists, with no trace.
        attempt.State.ShouldBe(RunningMutexState.Refused);
        attempt.Handle.ShouldBeNull();
    }
}

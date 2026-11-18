using LotroKoniecDev.DesktopApp.Presentation.ViewModels;

namespace LotroKoniecDev.DesktopApp.Tests.Unit.Tests;

public sealed class OperationGateTests
{
    [Fact]
    public void TryEnter_AFreeGate_TakesIt()
    {
        // Arrange
        OperationGate gate = new();

        // Act
        bool entered = gate.TryEnter();

        // Assert
        entered.ShouldBeTrue();
        gate.IsBusy.ShouldBeTrue();
    }

    [Fact]
    public void TryEnter_ATakenGate_IsRefused()
    {
        // Arrange
        OperationGate gate = new();
        gate.TryEnter();

        // Act
        bool entered = gate.TryEnter();

        // Assert
        entered.ShouldBeFalse();
        gate.IsBusy.ShouldBeTrue();
    }

    [Fact]
    public void Exit_ATakenGate_FreesItForTheNextOperation()
    {
        // Arrange
        OperationGate gate = new();
        gate.TryEnter();

        // Act
        gate.Exit();

        // Assert
        gate.IsBusy.ShouldBeFalse();
        gate.TryEnter().ShouldBeTrue();
    }

    [Fact]
    public void IsBusy_EnterAndExitTwice_TellsTheBoundButtonsOncePerChange()
    {
        // Arrange
        OperationGate gate = new();
        List<string?> changed = [];
        gate.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        // Act
        gate.TryEnter();
        gate.TryEnter();
        gate.Exit();
        gate.Exit();

        // Assert: one notice per real change, none for a refused enter or a second exit.
        changed.ShouldBe([nameof(OperationGate.IsBusy), nameof(OperationGate.IsBusy)]);
    }
}

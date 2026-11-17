using LotroKoniecDev.DesktopApp.Presentation.ViewModels;

namespace LotroKoniecDev.DesktopApp.Tests.Unit.Tests;

public sealed class DelegateCommandTests
{
    [Fact]
    public void Execute_WhenAllowed_RunsTheAction()
    {
        // Arrange
        int runs = 0;
        DelegateCommand command = new(() => runs++, () => true);

        // Act
        command.Execute(null);

        // Assert
        runs.ShouldBe(1);
    }

    [Fact]
    public void Execute_WhenNotAllowed_DoesNothing()
    {
        // Arrange: a second click on "Tak" that reaches the command while it is disabled.
        int runs = 0;
        DelegateCommand command = new(() => runs++, () => false);

        // Act
        command.Execute(null);

        // Assert
        runs.ShouldBe(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CanExecute_AnyAnswer_IsTheAnswerOfItsCheck(bool allowed)
    {
        // Arrange
        DelegateCommand command = new(() => { }, () => allowed);

        // Act
        bool canExecute = command.CanExecute(null);

        // Assert
        canExecute.ShouldBe(allowed);
    }

    [Fact]
    public void RaiseCanExecuteChanged_Always_TellsTheButton()
    {
        // Arrange
        DelegateCommand command = new(() => { }, () => true);
        int notices = 0;
        command.CanExecuteChanged += (_, _) => notices++;

        // Act
        command.RaiseCanExecuteChanged();

        // Assert
        notices.ShouldBe(1);
    }
}

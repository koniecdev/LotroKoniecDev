using System.ComponentModel;

namespace LotroKoniecDev.DesktopApp.Presentation.ViewModels;

/// <summary>
/// One operation at a time for everything that writes: a patch, a launch, an update, a save of the
/// settings. Scoped, like the progress bar they report to, so they share one gate. The update holds it
/// from "Tak" until the app closes.
/// </summary>
/// <remarks>Used from the UI thread only, so it needs no lock.</remarks>
public sealed class OperationGate : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsBusy
    {
        get;
        private set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBusy)));
        }
    }

    /// <summary>Takes the gate when it is free; returns false and changes nothing when it is not.</summary>
    public bool TryEnter()
    {
        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;
        return true;
    }

    public void Exit() => IsBusy = false;
}

namespace CanYeAye.App.Services;

/// <summary>Window-provided file dialogs; keeps the view model testable.</summary>
public interface IFileDialogService
{
    Task<string?> PickOpenLogAsync();

    Task<string?> PickSaveLogAsync(string suggestedName);

    Task<string?> PickOpenDbcAsync();

    Task<string?> PickSaveDbcAsync(string suggestedName);

    Task<IReadOnlyList<string>> PickOpenDbcsAsync();

    Task<string?> PickOpenScriptAsync();

    Task<string?> PickSaveScriptAsync(string suggestedName);

    Task<string?> PickOpenNodeNamesAsync();

    Task<string?> PickSaveNodeNamesAsync(string suggestedName);

    Task<string?> PickSaveTextAsync(string suggestedName);

    Task ShowMessageAsync(string title, string message);

    Task<bool> ConfirmAsync(string title, string message);

    /// <summary>Confirmation with custom button labels; true when the accept button is pressed.</summary>
    Task<bool> ConfirmAsync(string title, string message, string acceptText, string declineText);
}

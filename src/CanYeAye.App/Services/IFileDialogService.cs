namespace CanYeAye.App.Services;

/// <summary>Window-provided file dialogs; keeps the view model testable.</summary>
public interface IFileDialogService
{
    Task<string?> PickOpenLogAsync();

    Task<string?> PickSaveLogAsync(string suggestedName);

    Task<string?> PickOpenDbcAsync();

    Task<IReadOnlyList<string>> PickOpenDbcsAsync();

    Task<string?> PickOpenScriptAsync();

    Task<string?> PickSaveScriptAsync(string suggestedName);

    Task<string?> PickOpenNodeNamesAsync();

    Task<string?> PickSaveNodeNamesAsync(string suggestedName);

    Task<string?> PickSaveTextAsync(string suggestedName);

    Task ShowMessageAsync(string title, string message);
}

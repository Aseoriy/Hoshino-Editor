namespace HoshinoEditor.Controls;

public sealed record ToastMessage(string Message, bool IsError = false);

public interface IEditorWorkspace
{
    string Title { get; }
    string Status { get; }
    event EventHandler<string>? TitleChanged;
    event EventHandler<string>? StatusChanged;
    event EventHandler? HomeRequested;
    event EventHandler<ToastMessage>? ToastRequested;
    void Open();
    void Save();
    void Undo();
    void Redo();
    void TogglePlayback();
    void CancelActiveTool();
    void Close();
}

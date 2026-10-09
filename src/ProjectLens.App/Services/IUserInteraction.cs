namespace ProjectLens.App.Services;

public interface IUserInteraction
{
    string? ChooseInputFolder();
    string? ChooseInputFile();
    string? ChooseOutputFolder();
    void CopyText(string text);
}

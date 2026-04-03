namespace CDArchive.Core.Services;

public interface IFileSystemService
{
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
    IEnumerable<string> EnumerateDirectories(string path);
    IEnumerable<string> EnumerateFiles(string path, string searchPattern);
    string GetFileName(string path);
    string GetFileNameWithoutExtension(string path);
    string GetDirectoryName(string path);
    string CombinePath(params string[] paths);
}

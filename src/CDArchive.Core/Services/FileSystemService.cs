namespace CDArchive.Core.Services;

public class FileSystemService : IFileSystemService
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public IEnumerable<string> EnumerateDirectories(string path) => Directory.EnumerateDirectories(path);

    public IEnumerable<string> EnumerateFiles(string path, string searchPattern) =>
        Directory.EnumerateFiles(path, searchPattern);

    public string GetFileName(string path) => Path.GetFileName(path);

    public string GetFileNameWithoutExtension(string path) => Path.GetFileNameWithoutExtension(path);

    public string GetDirectoryName(string path) => Path.GetDirectoryName(path)!;

    public string CombinePath(params string[] paths) => Path.Combine(paths);
}

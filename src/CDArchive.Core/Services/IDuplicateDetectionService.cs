namespace CDArchive.Core.Services;

public interface IDuplicateDetectionService
{
    List<string> FindPotentialDuplicates(string albumName);
}

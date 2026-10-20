namespace OrbitConvert.App.Services;

public interface IFileMetadataProvider
{
    FileMetadataResult GetMetadata(string path);
}

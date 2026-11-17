using System.Text;

namespace Minichat.Protocol;

public static class AtomicFile
{
    public static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content, Encoding.UTF8);
        File.Move(temporary, path, overwrite: true);
    }
}

namespace MiniRP.Protocol;

public static class PluginSecretNames
{
    public const string Base = "builtin-base";

    public static bool ValidId(string id) => id.Length is > 0 and <= 128 &&
        id.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-');
}

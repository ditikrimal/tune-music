using System.Text.Json.Nodes;

namespace Tune.Core.Tests;

internal static class Fixture
{
    public static string Text(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static JsonNode Load(string name) => JsonNode.Parse(Text(name))!;
}

#!/usr/bin/env dotnet run
#:package YamlDotNet@17.0.1

// Refreshes src/Dolphin/Lsp/semgrep-schema.json from the pinned upstream commit.
// Run: dotnet run scripts/update-schema.cs
//
// rule_schema_v1.yaml is sourced from https://github.com/returntocorp/semgrep-interfaces
// (LGPL-2.1). The only modifications applied here are:
//   1. YAML → JSON conversion via YamlDotNet.
//   2. $schema URI set to draft 2019-09 (the draft JsonSchema.Net expects).
//   3. focus-metavariable restructure: upstream YAML emits an invalid
//      {items: {OneOf: [string, array]}} fragment; we rewrite it to the
//      well-formed {oneOf: [{type: string}, {type: array}]} that JsonSchema.Net
//      can enforce. Drop this patch if upstream ever fixes their YAML.
//
// Note: YamlDotNet uses YAML 1.2, which does NOT treat `on`/`off`/`yes`/`no`
// as booleans (YAML 1.1 did). Output will therefore preserve keys like `on:`
// as the string "on" rather than coercing to "true". This matches how Dolphin
// reads user rule files at runtime, so schema keys and rule-file keys line up.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Serialization;

const string Commit   = "656d6518e822a088c6e1907abf7c3acc61095103";
const string OutPath  = "src/Dolphin/Lsp/semgrep-schema.json";
var          url      = $"https://raw.githubusercontent.com/returntocorp/semgrep-interfaces/{Commit}/rule_schema_v1.yaml";

using var http = new HttpClient();
var yaml = await http.GetStringAsync(url);

var obj = new DeserializerBuilder()
    .WithAttemptingUnquotedStringTypeDeserialization()
    .Build()
    .Deserialize<object>(yaml);

var jsonText = new SerializerBuilder().JsonCompatible().Build().Serialize(obj);
var root = JsonNode.Parse(jsonText)!.AsObject();

root["$schema"] = "https://json-schema.org/draft/2019-09/schema";

// focus-metavariable hand-patch (see header comment)
var focus = root["$defs"]?["focus-metavariable"]?["properties"]?["focus-metavariable"]?.AsObject();
if (focus is not null && focus["items"] is not null)
{
    focus.Remove("items");
    focus["oneOf"] = new JsonArray(
        new JsonObject { ["type"] = "string" },
        new JsonObject { ["type"] = "array"  });
}

var opts = new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder       = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};
File.WriteAllText(OutPath, root.ToJsonString(opts));
Console.WriteLine($"Wrote {OutPath}");

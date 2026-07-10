namespace NetHack.Api

open System.Text.Json
open System.Text.Json.Serialization

/// Serialization for the wire types. Discriminated unions are encoded with an
/// internal "type" tag and named fields, so `Prompt`, `Direction`, etc. render
/// as clean, self-describing JSON. Fieldless cases (e.g. Command, North) become
/// plain strings. `None` fields are omitted rather than written as null.
module Json =

    let options : JsonSerializerOptions =
        let fsharp =
            JsonFSharpOptions.Default()
                .WithUnionInternalTag()          // discriminator lives inside the object
                .WithUnionNamedFields()          // fields by name, not "Item1"
                .WithUnionTagName("type")        // { "type": "YesNo", ... }
                .WithUnionUnwrapFieldlessTags()  // Command -> "Command", North -> "North"
                .WithSkippableOptionFields()     // None -> field omitted
        let o = JsonSerializerOptions(JsonSerializerDefaults.Web)
        o.WriteIndented <- true
        o.Converters.Add(JsonFSharpConverter(fsharp))
        o

    /// Serialize any wire type (Observation, GameState, Prompt, ...) to JSON.
    let toJson (value: 'T) : string =
        JsonSerializer.Serialize(value, options)

    /// Deserialize a wire type from JSON.
    let ofJson<'T> (json: string) : 'T =
        JsonSerializer.Deserialize<'T>(json, options)

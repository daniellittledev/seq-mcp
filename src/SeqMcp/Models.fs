namespace SeqMcp

open System.Text.Json
open System.Text.Json.Serialization

/// Result of GET /api/data.
/// Only one of Rows / Slices / Series is populated for a given result set;
/// this server renders the tabular Rows shape (the one produced by plain
/// selects and `group by <expr>` aggregates).
type QueryResult =
    { [<JsonPropertyName("Columns")>]
      Columns: string[]

      [<JsonPropertyName("Rows")>]
      Rows: JsonElement[][]

      /// Populated instead of Rows when the query groups by time(...).
      [<JsonPropertyName("Slices")>]
      Slices: JsonElement

      /// Query-level error message, when the query could not be executed.
      [<JsonPropertyName("Error")>]
      Error: string

      [<JsonPropertyName("Reasons")>]
      Reasons: string[] }

/// A single property attached to an event (Name = Value).
type EventProperty =
    { [<JsonPropertyName("Name")>]
      Name: string

      [<JsonPropertyName("Value")>]
      Value: JsonElement }

/// A rendered event from GET /api/events (render=true) or /api/events/{id}.
/// Level is omitted by Seq for Information-level events; Exception/Properties
/// are populated for the single-event detail view.
type SeqEvent =
    { [<JsonPropertyName("Id")>]
      Id: string

      [<JsonPropertyName("Timestamp")>]
      Timestamp: string

      [<JsonPropertyName("Level")>]
      Level: string

      [<JsonPropertyName("RenderedMessage")>]
      RenderedMessage: string

      [<JsonPropertyName("Exception")>]
      Exception: string

      [<JsonPropertyName("Properties")>]
      Properties: EventProperty[] }

/// A saved Seq signal (a named, reusable filter) from GET /api/signals.
type Signal =
    { [<JsonPropertyName("Id")>]
      Id: string

      [<JsonPropertyName("Title")>]
      Title: string

      [<JsonPropertyName("Description")>]
      Description: string }

/// Runtime activity embedded in each alert from GET /api/alerts: the outcome of
/// the most recent evaluation plus cumulative counts. Readable with a plain read
/// API key, unlike the /api/alertstate endpoint (which requires Project permission).
type AlertActivity =
    { [<JsonPropertyName("LastCheck")>]
      LastCheck: string

      [<JsonPropertyName("LastCheckTriggered")>]
      LastCheckTriggered: bool

      [<JsonPropertyName("SuppressedUntil")>]
      SuppressedUntil: string

      [<JsonPropertyName("TotalOccurrences")>]
      TotalOccurrences: int }

/// A configured Seq alert (a named rule that triggers on matching events) from
/// GET /api/alerts. SignalExpression/NotificationChannels are kept as raw JSON
/// elements because their shape is server-defined; they render via Render.cell.
type Alert =
    { [<JsonPropertyName("Id")>]
      Id: string

      [<JsonPropertyName("Title")>]
      Title: string

      [<JsonPropertyName("Description")>]
      Description: string

      [<JsonPropertyName("IsDisabled")>]
      IsDisabled: bool

      [<JsonPropertyName("IsProtected")>]
      IsProtected: bool

      [<JsonPropertyName("OwnerId")>]
      OwnerId: string

      [<JsonPropertyName("Where")>]
      Where: string

      [<JsonPropertyName("Having")>]
      Having: string

      [<JsonPropertyName("TimeGrouping")>]
      TimeGrouping: string

      [<JsonPropertyName("NotificationLevel")>]
      NotificationLevel: string

      [<JsonPropertyName("SignalExpression")>]
      SignalExpression: JsonElement

      [<JsonPropertyName("NotificationChannels")>]
      NotificationChannels: JsonElement[]

      [<JsonPropertyName("Activity")>]
      Activity: AlertActivity }

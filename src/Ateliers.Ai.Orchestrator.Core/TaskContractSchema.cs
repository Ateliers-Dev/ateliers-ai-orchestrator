using System.Text.Json;

namespace Ateliers.Ai.Orchestrator.Core;

/// <summary>TaskContractのJSON SchemaをCoreの契約型・検証規則に沿って提供します。</summary>
public static class TaskContractSchema
{
    private const string Schema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "$id": "https://ateliers.dev/schemas/task-contract-v1.json",
      "title": "Ateliers AI Orchestrator TaskContract",
      "type": "object",
      "additionalProperties": false,
      "required": ["schemaVersion", "taskId", "purpose", "workingDirectory", "scope", "inputs", "requirements", "acceptanceCriteria", "delegatedOperations", "limits", "stopConditions", "reporting"],
      "properties": {
        "schemaVersion": { "type": "integer", "const": 1 },
        "taskId": { "type": "string", "minLength": 1, "pattern": "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$" },
        "purpose": { "type": "string", "minLength": 1, "pattern": "^.*\\S.*$" },
        "workingDirectory": { "type": "string", "minLength": 1, "pattern": "^(?:[A-Za-z]:[\\\\/]|\\\\\\\\[^\\\\/]+[\\\\/][^\\\\/]+|/).*$" },
        "scope": { "$ref": "#/$defs/taskScope" },
        "inputs": { "type": "array", "items": { "$ref": "#/$defs/taskInput" } },
        "requirements": { "type": "array", "minItems": 1, "items": { "$ref": "#/$defs/nonBlankString" } },
        "acceptanceCriteria": { "type": "array", "minItems": 1, "items": { "$ref": "#/$defs/nonBlankString" } },
        "delegatedOperations": { "type": "array", "items": { "$ref": "#/$defs/nonBlankString" } },
        "limits": { "$ref": "#/$defs/executionLimits" },
        "stopConditions": { "type": "array", "minItems": 1, "items": { "$ref": "#/$defs/nonBlankString" } },
        "reporting": { "$ref": "#/$defs/reportingRequirements" }
      },
      "$defs": {
        "nonBlankString": { "type": "string", "minLength": 1, "pattern": "^.*\\S.*$" },
        "taskScope": {
          "type": "object", "additionalProperties": false, "required": ["included", "excluded"],
          "properties": {
            "included": { "type": "array", "minItems": 1, "items": { "$ref": "#/$defs/nonBlankString" } },
            "excluded": { "type": "array", "items": { "$ref": "#/$defs/nonBlankString" } }
          }
        },
        "taskInput": {
          "type": "object", "additionalProperties": false, "required": ["description", "reference", "revision"],
          "properties": {
            "description": { "$ref": "#/$defs/nonBlankString" },
            "reference": { "$ref": "#/$defs/nonBlankString" },
            "revision": { "$ref": "#/$defs/nonBlankString" }
          }
        },
        "executionLimits": {
          "type": "object", "additionalProperties": false, "required": ["maxDurationMinutes", "maxRevisionAttempts", "maxRetries"],
          "properties": {
            "maxDurationMinutes": { "type": "integer", "minimum": 1, "maximum": 2147483647 },
            "maxRevisionAttempts": { "type": "integer", "minimum": 0, "maximum": 2147483647 },
            "maxRetries": { "type": "integer", "minimum": 0, "maximum": 2147483647 }
          }
        },
        "reportingRequirements": {
          "type": "object", "additionalProperties": false, "required": ["pullRequestExpected", "finalReportFormat"],
          "properties": {
            "pullRequestExpected": { "type": "boolean" },
            "finalReportFormat": { "type": "string", "minLength": 1, "pattern": "^[Mm][Aa][Rr][Kk][Dd][Oo][Ww][Nn]$" }
          }
        }
      }
    }
    """;

    /// <summary>TaskContract v1のJSON Schemaを返します。</summary>
    public static string GetJson()
    {
        using var document = JsonDocument.Parse(Schema);
        return JsonSerializer.Serialize(document.RootElement);
    }
}

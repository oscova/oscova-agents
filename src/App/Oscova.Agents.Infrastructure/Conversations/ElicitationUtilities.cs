using System.Numerics;
using Oscova.Agents.Conversations;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using System.Globalization;
using System.Text.Json;

namespace Oscova.Agents.Infrastructure.Conversations;

public static class ElicitationUtilities
{
    public static IReadOnlyList<ElicitationBooleanField> CreateBooleanFields(
        IDictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition> properties)
    {
        var fields = new List<ElicitationBooleanField>();

        foreach (var property in properties)
        {
            if (property.Value is not ElicitRequestParams.BooleanSchema booleanSchema)
            {
                continue;
            }

            fields.Add(new ElicitationBooleanField
            {
                Name = property.Key,
                Label = string.IsNullOrWhiteSpace(booleanSchema.Title) ? property.Key : booleanSchema.Title!,
                Description = booleanSchema.Description
            });
        }

        return fields;
    }

    public static IReadOnlyList<ElicitationStringField> CreateStringFields(
        IDictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition> properties)
    {
        var fields = new List<ElicitationStringField>();

        foreach (var property in properties)
        {
            if (property.Value is not ElicitRequestParams.StringSchema stringSchema)
            {
                continue;
            }

            fields.Add(new ElicitationStringField
            {
                Name = property.Key,
                Label = string.IsNullOrWhiteSpace(stringSchema.Title) ? property.Key : stringSchema.Title!,
                Description = stringSchema.Description,
                MinLength = stringSchema.MinLength,
                MaxLength = stringSchema.MaxLength,
                Format = stringSchema.Format
            });
        }

        return fields;
    }

    public static IReadOnlyList<ElicitationNumberField> CreateNumberFields(
        IDictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition> properties)
    {
        var fields = new List<ElicitationNumberField>();

        foreach (var property in properties)
        {
            if (property.Value is not ElicitRequestParams.NumberSchema numberSchema)
            {
                continue;
            }

            fields.Add(new ElicitationNumberField
            {
                Name = property.Key,
                Label = string.IsNullOrWhiteSpace(numberSchema.Title) ? property.Key : numberSchema.Title!,
                Description = numberSchema.Description,
                Minimum = numberSchema.Minimum,
                Maximum = numberSchema.Maximum,
                IsInteger = string.Equals(numberSchema.Type, "integer", StringComparison.OrdinalIgnoreCase)
            });
        }

        return fields;
    }

    public static IReadOnlyList<ElicitationEnumField> CreateEnumFields(
        IDictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition> properties)
    {
        var fields = new List<ElicitationEnumField>();

        foreach (var property in properties)
        {
            var options = new List<ElicitationEnumOption>();
            string? title = null;
            string? description = null;
            string? defaultValue = null;

            switch (property.Value)
            {
                case ElicitRequestParams.TitledSingleSelectEnumSchema titledSchema:
                    title = titledSchema.Title;
                    description = titledSchema.Description;
                    defaultValue = titledSchema.Default;
                    if (titledSchema.OneOf is not null)
                    {
                        foreach (var option in titledSchema.OneOf)
                        {
                            options.Add(new ElicitationEnumOption
                            {
                                Value = option.Const,
                                Label = string.IsNullOrWhiteSpace(option.Title) ? option.Const : option.Title
                            });
                        }
                    }

                    break;

                case ElicitRequestParams.UntitledSingleSelectEnumSchema untitledSchema:
                    title = untitledSchema.Title;
                    description = untitledSchema.Description;
                    defaultValue = untitledSchema.Default;
                    if (untitledSchema.Enum is not null)
                    {
                        foreach (var value in untitledSchema.Enum)
                        {
                            options.Add(new ElicitationEnumOption
                            {
                                Value = value,
                                Label = value
                            });
                        }
                    }

                    break;

                default:
                    continue;
            }

            fields.Add(new ElicitationEnumField
            {
                Name = property.Key,
                Label = string.IsNullOrWhiteSpace(title) ? property.Key : title!,
                Description = description,
                Default = defaultValue,
                Options = options
            });
        }

        return fields;
    }

    public static bool TryDetectSchemaType(
        IDictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition> properties,
        ILogger logger,
        out ElicitationSchemaType schemaType)
    {
        schemaType = default;
        ElicitationSchemaType? detectedType = null;

        foreach (var property in properties)
        {
            var currentType = property.Value switch
            {
                ElicitRequestParams.BooleanSchema => ElicitationSchemaType.Boolean,
                ElicitRequestParams.StringSchema => ElicitationSchemaType.String,
                ElicitRequestParams.NumberSchema => ElicitationSchemaType.Number,
                ElicitRequestParams.TitledSingleSelectEnumSchema => ElicitationSchemaType.Enum,
                ElicitRequestParams.UntitledSingleSelectEnumSchema => ElicitationSchemaType.Enum,
                _ => (ElicitationSchemaType?)null
            };

            if (currentType is null)
            {
                logger.LogWarning("Unsupported elicitation schema type '{SchemaType}'.", property.Value?.GetType().Name ?? "unknown");
                return false;
            }

            if (detectedType is null)
            {
                detectedType = currentType;
            }
            else if (detectedType != currentType)
            {
                logger.LogWarning("Received an elicitation request containing mixed schema types. Existing {ExistingType}, new {NewType}.", detectedType, currentType);
                return false;
            }
        }

        if (detectedType is null)
        {
            logger.LogWarning("Elicitation request schema type could not be determined.");
            return false;
        }

        schemaType = detectedType.Value;
        return true;
    }

    public static IReadOnlySet<string> ExtractRequiredFields(ElicitRequestParams requestParams)
    {
        return requestParams.RequestedSchema?.Required is { Count: > 0 } required
            ? new HashSet<string>(required, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public static string GetDefaultMessage(ElicitationSchemaType schemaType, string? customMessage)
    {
        if (!string.IsNullOrWhiteSpace(customMessage))
        {
            return customMessage;
        }

        return schemaType switch
        {
            ElicitationSchemaType.Boolean => "The tool needs a quick confirmation.",
            ElicitationSchemaType.String => "The tool needs a short text response.",
            ElicitationSchemaType.Number => "The tool needs a number from you.",
            ElicitationSchemaType.Enum => "Please choose one of the available options.",
            _ => "The tool needs additional information."
        };
    }

    public static bool ValidateElicitationRequest(ElicitRequestParams requestParams, ILogger logger)
    {
        if (requestParams.RequestedSchema?.Properties is null || requestParams.RequestedSchema.Properties.Count == 0)
        {
            logger.LogWarning("Received an elicitation request without properties.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Converts MCP schema parameters into the app-owned elicitation request model shared by Web and text channels.
    /// </summary>
    public static ElicitationRequest? BuildRequest(ElicitRequestParams requestParams, ILogger logger)
    {
        if (!ValidateElicitationRequest(requestParams, logger))
        {
            return null;
        }

        var properties = requestParams.RequestedSchema!.Properties!;
        if (!TryDetectSchemaType(properties, logger, out var schemaType))
        {
            return null;
        }

        var request = new ElicitationRequest
        {
            RequestId = Guid.NewGuid(),
            Message = GetDefaultMessage(schemaType, requestParams.Message),
            SchemaType = schemaType,
            RequiredFields = ExtractRequiredFields(requestParams)
        };

        switch (schemaType)
        {
            case ElicitationSchemaType.Boolean:
                var booleanFields = CreateBooleanFields(properties);
                if (booleanFields.Count == 0)
                {
                    logger.LogWarning("Elicitation request had no boolean fields to render.");
                    return null;
                }

                request.BooleanFields = booleanFields;
                break;

            case ElicitationSchemaType.String:
                var stringFields = CreateStringFields(properties);
                if (stringFields.Count == 0)
                {
                    logger.LogWarning("Elicitation request had no string fields to render.");
                    return null;
                }

                request.StringFields = stringFields;
                break;

            case ElicitationSchemaType.Number:
                var numberFields = CreateNumberFields(properties);
                if (numberFields.Count == 0)
                {
                    logger.LogWarning("Elicitation request had no numeric fields to render.");
                    return null;
                }

                request.NumberFields = numberFields;
                break;

            case ElicitationSchemaType.Enum:
                var enumFields = CreateEnumFields(properties);
                if (enumFields.Count == 0)
                {
                    logger.LogWarning("Elicitation request had no enum fields to render.");
                    return null;
                }

                foreach (var field in enumFields)
                {
                    if (field.Options.Count == 0)
                    {
                        logger.LogWarning("Enum elicitation field '{Field}' did not specify any options.", field.Name);
                        return null;
                    }
                }

                request.EnumFields = enumFields;
                break;

            default:
                logger.LogWarning("Unsupported elicitation schema type '{SchemaType}'.", schemaType);
                return null;
        }

        return request;
    }

    /// <summary>Validates a reply against the retained request before it can complete pending work.</summary>
    public static void ValidateResponse(ElicitationRequest request, IDictionary<string, JsonElement> content)
    {
        foreach (var name in request.RequiredFields)
        {
            if (!content.ContainsKey(name))
            {
                throw new ArgumentException($"Field '{name}' is required.");
            }
        }

        foreach (var field in request.StringFields)
        {
            if (!content.TryGetValue(field.Name, out var value)) { continue; }

            var text = value.GetString() ?? string.Empty;
            if (request.IsFieldRequired(field.Name) && string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException($"Field '{field.Name}' is required.");
            }

            if (field.MinLength is { } min && text.Length < min)
            {
                throw new ArgumentException($"Field '{field.Name}' requires at least {min} characters.");
            }

            if (field.MaxLength is { } max && text.Length > max)
            {
                throw new ArgumentException($"Field '{field.Name}' permits at most {max} characters.");
            }
        }

        foreach (var field in request.NumberFields)
        {
            if (!content.TryGetValue(field.Name, out var value)) { continue; }

            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            {
                throw new ArgumentException($"Field '{field.Name}' requires a finite number.");
            }

            if (field.IsInteger)
            {
                // Integer JSON and bound comparisons must never round through a floating-point value.
                if (!BigInteger.TryParse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var integer) ||
                    integer < long.MinValue || integer > long.MaxValue)
                {
                    throw new ArgumentException($"Field '{field.Name}' requires a 64-bit integer.");
                }

                if (field.Minimum is { } min && integer < new BigInteger(Math.Ceiling(min)))
                {
                    throw new ArgumentException($"Field '{field.Name}' must be at least {min}.");
                }

                if (field.Maximum is { } max && integer > new BigInteger(Math.Floor(max)))
                {
                    throw new ArgumentException($"Field '{field.Name}' must be at most {max}.");
                }

                content[field.Name] = JsonSerializer.SerializeToElement((long)integer);
            }
            else
            {
                if (field.Minimum is { } min && number < min)
                {
                    throw new ArgumentException($"Field '{field.Name}' must be at least {min}.");
                }

                if (field.Maximum is { } max && number > max)
                {
                    throw new ArgumentException($"Field '{field.Name}' must be at most {max}.");
                }
            }
        }

        foreach (var field in request.EnumFields)
        {
            if (content.TryGetValue(field.Name, out var value) &&
                !field.Options.Any(option => string.Equals(option.Value, value.GetString(), StringComparison.Ordinal)))
            {
                throw new ArgumentException($"Field '{field.Name}' requires one of its listed options.");
            }
        }
    }

    // Builds a typed content dictionary for a boolean elicitation result.
    public static IDictionary<string, JsonElement> BuildBooleanContent(IReadOnlyList<ElicitationBooleanField> fields, bool value)
    {
        var content = new Dictionary<string, JsonElement>(fields.Count);
        foreach (var field in fields)
        {
            content[field.Name] = JsonSerializer.SerializeToElement(value);
        }

        return content;
    }

    // Builds a typed content dictionary from raw string values. Unknown field names are silently ignored.
    public static IDictionary<string, JsonElement> BuildStringContent(
        IReadOnlyList<ElicitationStringField> fields,
        IReadOnlyDictionary<string, string> rawValues)
    {
        var knownNames = new HashSet<string>(fields.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        var content = new Dictionary<string, JsonElement>();
        foreach (var pair in rawValues)
        {
            if (knownNames.Contains(pair.Key))
                content[pair.Key] = JsonSerializer.SerializeToElement(pair.Value ?? string.Empty);
        }

        return content;
    }

    // Builds a typed content dictionary from raw text values, parsing integers vs floats per field metadata.
    // Fields whose text cannot be parsed to the expected numeric type are omitted from the result.
    public static IDictionary<string, JsonElement> BuildNumberContent(
        IReadOnlyList<ElicitationNumberField> fields,
        IReadOnlyDictionary<string, string> rawValues)
    {
        var content = new Dictionary<string, JsonElement>();
        foreach (var pair in rawValues)
        {
            var field = fields.FirstOrDefault(f => string.Equals(f.Name, pair.Key, StringComparison.OrdinalIgnoreCase));
            if (field is null) { continue; }

            if (field.IsInteger)
            {
                if (long.TryParse(pair.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
                {
                    content[pair.Key] = JsonSerializer.SerializeToElement(intValue);
                }
            }
            else if (double.TryParse(pair.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
            {
                content[pair.Key] = JsonSerializer.SerializeToElement(doubleValue);
            }
        }

        return content;
    }

    // Builds a typed content dictionary for an enum elicitation result. Unknown field names are silently ignored.
    public static IDictionary<string, JsonElement> BuildEnumContent(
        IReadOnlyList<ElicitationEnumField> fields,
        IReadOnlyDictionary<string, string> rawValues)
    {
        var knownNames = new HashSet<string>(fields.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        var content = new Dictionary<string, JsonElement>();
        foreach (var pair in rawValues)
        {
            if (knownNames.Contains(pair.Key))
            {
                content[pair.Key] = JsonSerializer.SerializeToElement(pair.Value ?? string.Empty);
            }
        }

        return content;
    }
}

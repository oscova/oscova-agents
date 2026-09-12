using System;
using System.Collections.Generic;

namespace Oscova.Agents.Conversations;

/// <summary>Represents a structured request for user input.</summary>
public class ElicitationRequest
{
    private IReadOnlyList<ElicitationBooleanField> _booleanFields = [];
    private IReadOnlyList<ElicitationStringField> _stringFields = [];
    private IReadOnlyList<ElicitationNumberField> _numberFields = [];
    private IReadOnlyList<ElicitationEnumField> _enumFields = [];
    private HashSet<string> _requiredFields = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the request correlation identifier.</summary>
    public Guid RequestId { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the message shown to the user.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets the elicitation schema type.</summary>
    public ElicitationSchemaType SchemaType { get; set; } = ElicitationSchemaType.Boolean;

    /// <summary>Gets or sets the requested Boolean fields.</summary>
    public IReadOnlyList<ElicitationBooleanField> BooleanFields
    {
        get => _booleanFields;
        set => _booleanFields = value ?? [];
    }

    /// <summary>Gets or sets the requested string fields.</summary>
    public IReadOnlyList<ElicitationStringField> StringFields
    {
        get => _stringFields;
        set => _stringFields = value ?? [];
    }

    /// <summary>Gets or sets the requested number fields.</summary>
    public IReadOnlyList<ElicitationNumberField> NumberFields
    {
        get => _numberFields;
        set => _numberFields = value ?? [];
    }

    /// <summary>Gets or sets the requested enum fields.</summary>
    public IReadOnlyList<ElicitationEnumField> EnumFields
    {
        get => _enumFields;
        set => _enumFields = value ?? [];
    }

    /// <summary>Gets or sets the required schema field names.</summary>
    public IReadOnlyCollection<string> RequiredFields
    {
        get => _requiredFields;
        set => _requiredFields = value is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Determines whether a field is required.</summary>
    public bool IsFieldRequired(string fieldName) => _requiredFields.Contains(fieldName);
}

/// <summary>Identifies the primitive schema used by an elicitation request.</summary>
public enum ElicitationSchemaType
{
    /// <summary>Represents a Boolean schema.</summary>
    Boolean,
    /// <summary>Represents a string schema.</summary>
    String,
    /// <summary>Represents a number schema.</summary>
    Number,
    /// <summary>Represents an enum schema.</summary>
    Enum
}

/// <summary>Describes a Boolean value requested by an elicitation schema.</summary>
public class ElicitationBooleanField
{
    /// <summary>Gets or sets the field name used in the response payload.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the field label.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets optional field help text.</summary>
    public string? Description { get; set; }
}

/// <summary>Describes a string value requested by an elicitation schema.</summary>
public class ElicitationStringField
{
    /// <summary>Gets or sets the field name used in the response payload.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the field label.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets optional field help text.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the minimum accepted length.</summary>
    public int? MinLength { get; set; }

    /// <summary>Gets or sets the maximum accepted length.</summary>
    public int? MaxLength { get; set; }

    /// <summary>Gets or sets the expected string format.</summary>
    public string? Format { get; set; }

    /// <summary>Gets or sets the default string value.</summary>
    public string? Default { get; set; }
}

/// <summary>Describes a numeric value requested by an elicitation schema.</summary>
public class ElicitationNumberField
{
    /// <summary>Gets or sets the field name used in the response payload.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the field label.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets optional field help text.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the minimum accepted value.</summary>
    public double? Minimum { get; set; }

    /// <summary>Gets or sets the maximum accepted value.</summary>
    public double? Maximum { get; set; }

    /// <summary>Gets or sets the default numeric value.</summary>
    public double? Default { get; set; }

    /// <summary>Gets or sets whether the field accepts only integer values.</summary>
    public bool IsInteger { get; set; }
}

/// <summary>Describes an enum value requested by an elicitation schema.</summary>
public class ElicitationEnumField
{
    /// <summary>Gets or sets the field name used in the response payload.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the field label.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets optional field help text.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the default option value.</summary>
    public string? Default { get; set; }

    /// <summary>Gets or sets the selectable options.</summary>
    public IReadOnlyList<ElicitationEnumOption> Options { get; set; } = [];
}

/// <summary>Represents a selectable enum option.</summary>
public class ElicitationEnumOption
{
    /// <summary>Gets or sets the serialized option value.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Gets or sets the option label.</summary>
    public string Label { get; set; } = string.Empty;
}

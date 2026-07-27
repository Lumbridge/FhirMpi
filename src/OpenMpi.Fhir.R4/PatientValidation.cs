using System.Threading.Channels;
using Firely.Fhir.Validation;
using Hl7.Fhir.Model;
using Hl7.Fhir.Specification.Source;
using Hl7.Fhir.Specification.Terminology;
using OpenMpi.Application.Normalisation;

namespace OpenMpi.Fhir.R4;

public sealed record FhirValidationIssue(
    OperationOutcome.IssueSeverity Severity,
    OperationOutcome.IssueType Code,
    string Diagnostics,
    string? Expression = null);

public sealed record FhirValidationResult(IReadOnlyList<FhirValidationIssue> Issues)
{
    public bool IsSuccessful => Issues.All(static issue =>
        issue.Severity is not OperationOutcome.IssueSeverity.Error and
            not OperationOutcome.IssueSeverity.Fatal);

    public OperationOutcome ToOperationOutcome() =>
        new()
        {
            Issue = Issues.Select(static issue => new OperationOutcome.IssueComponent
            {
                Severity = issue.Severity,
                Code = issue.Code,
                Diagnostics = issue.Diagnostics,
                Expression = issue.Expression is null ? [] : [issue.Expression]
            }).ToList()
        };
}

public interface IPatientProfileValidator
{
    ValueTask<FhirValidationResult> ValidateWriteAsync(
        Patient patient,
        CancellationToken cancellationToken);

    ValueTask<FhirValidationResult> ValidateMatchInputAsync(
        Patient patient,
        CancellationToken cancellationToken);
}

public sealed class UkCorePatientValidator : IPatientProfileValidator
{
    public ValueTask<FhirValidationResult> ValidateWriteAsync(
        Patient patient,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var issues = ValidateStructure(patient, requireMatchInput: false);
        return ValueTask.FromResult(new FhirValidationResult(issues));
    }

    public ValueTask<FhirValidationResult> ValidateMatchInputAsync(
        Patient patient,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var issues = ValidateStructure(patient, requireMatchInput: true);
        return ValueTask.FromResult(new FhirValidationResult(issues));
    }

    private static List<FhirValidationIssue> ValidateStructure(
        Patient patient,
        bool requireMatchInput)
    {
        var issues = new List<FhirValidationIssue>();
        foreach (var identifier in patient.Identifier.Where(identifier =>
                     string.Equals(
                         identifier.System,
                         NhsNumberValidator.IdentifierSystem,
                         StringComparison.Ordinal)))
        {
            if (!NhsNumberValidator.IsValid(identifier.Value))
            {
                issues.Add(new FhirValidationIssue(
                    OperationOutcome.IssueSeverity.Error,
                    OperationOutcome.IssueType.Value,
                    "The NHS number is not valid under the modulus-11 check-digit algorithm.",
                    "Patient.identifier"));
            }
        }

        if (!string.IsNullOrWhiteSpace(patient.BirthDate) &&
            !DateOnly.TryParseExact(
                patient.BirthDate,
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out _))
        {
            issues.Add(new FhirValidationIssue(
                OperationOutcome.IssueSeverity.Error,
                OperationOutcome.IssueType.Value,
                "Patient.birthDate must be a complete calendar date.",
                "Patient.birthDate"));
        }

        if (requireMatchInput &&
            patient.Identifier.All(static identifier =>
                string.IsNullOrWhiteSpace(identifier.Value)) &&
            patient.Name.All(static name =>
                string.IsNullOrWhiteSpace(name.Family) &&
                name.Given.All(string.IsNullOrWhiteSpace)) &&
            string.IsNullOrWhiteSpace(patient.BirthDate) &&
            patient.Telecom.All(static telecom => string.IsNullOrWhiteSpace(telecom.Value)))
        {
            issues.Add(new FhirValidationIssue(
                OperationOutcome.IssueSeverity.Error,
                OperationOutcome.IssueType.Required,
                "At least one identifier, name, birth date, or telecom value is required for matching.",
                "Patient"));
        }

        return issues;
    }
}

public sealed class FirelyPatientValidatorPool : IPatientProfileValidator, IAsyncDisposable
{
    private readonly Channel<Validator> _validators;
    private readonly UkCorePatientValidator _minimumValidator = new();

    private FirelyPatientValidatorPool(IEnumerable<Validator> validators)
    {
        var materialised = validators.ToArray();
        if (materialised.Length == 0)
        {
            throw new ArgumentException("At least one Firely validator is required.", nameof(validators));
        }

        _validators = Channel.CreateBounded<Validator>(new BoundedChannelOptions(materialised.Length)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
        foreach (var validator in materialised)
        {
            if (!_validators.Writer.TryWrite(validator))
            {
                throw new InvalidOperationException("The validator pool could not be initialised.");
            }
        }
    }

    public static FirelyPatientValidatorPool Create(
        string packageDirectory,
        int poolSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        if (!Directory.Exists(packageDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The UK Core validation package directory '{packageDirectory}' does not exist.");
        }

        if (poolSize is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(poolSize));
        }

        return new FirelyPatientValidatorPool(
            Enumerable.Range(0, poolSize).Select(_ => CreateValidator(packageDirectory)));
    }

    public async ValueTask<FhirValidationResult> ValidateWriteAsync(
        Patient patient,
        CancellationToken cancellationToken)
    {
        var minimum = await _minimumValidator.ValidateWriteAsync(patient, cancellationToken);
        if (!minimum.IsSuccessful)
        {
            return minimum;
        }

        var validator = await _validators.Reader.ReadAsync(cancellationToken);
        try
        {
            var outcome = validator.Validate(patient, FhirR4Constants.UkCorePatientProfile);
            return new FhirValidationResult(outcome.Issue.Select(static issue =>
                new FhirValidationIssue(
                    issue.Severity ?? OperationOutcome.IssueSeverity.Error,
                    issue.Code ?? OperationOutcome.IssueType.Invalid,
                    issue.Diagnostics ?? issue.Details?.Text ?? "UK Core validation issue.",
                    issue.Expression.FirstOrDefault())).ToArray());
        }
        finally
        {
            await _validators.Writer.WriteAsync(validator, CancellationToken.None);
        }
    }

    public ValueTask<FhirValidationResult> ValidateMatchInputAsync(
        Patient patient,
        CancellationToken cancellationToken) =>
        _minimumValidator.ValidateMatchInputAsync(patient, cancellationToken);

    public ValueTask DisposeAsync()
    {
        _validators.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private static Validator CreateValidator(string packageDirectory)
    {
        var source = new DirectorySource(
            packageDirectory,
            new DirectorySourceSettings
            {
                IncludeSubDirectories = true
            });
        var resolver = new CachedResolver(source, 3600);
        var terminology = new LocalTerminologyService(resolver);
        return new Validator(resolver, terminology, null, null, null);
    }
}

using UnifyEmpi.Application;
using UnifyEmpi.Domain;
using UnifyEmpi.Portal;
using Xunit;

namespace UnifyEmpi.Portal.Tests;

public sealed class ReviewAssistantTests
{
    [Fact]
    public void ProbableAgreementProducesSupportedLinkGuidance()
    {
        var detail = Detail(
            Profile("9434765919", "Morgan", "Alex", new DateOnly(1984, 7, 12)),
            Profile("9434765919", "Morgan", "Alex", new DateOnly(1984, 7, 12)),
            MatchGrade.Probable,
            approvalPolicyLocked: false,
            similarity: 1);

        var assessment = ReviewAssistant.Assess(detail);

        Assert.Equal("Link is supported", assessment.Title);
        Assert.Equal("success", assessment.Tone);
        Assert.Equal(7, assessment.AgreementCount);
        Assert.Equal(0, assessment.ConflictCount);
    }

    [Fact]
    public void AuthoritativeConflictOverridesAggregateScore()
    {
        var detail = Detail(
            Profile("9434765919", "Morgan", "Alex", new DateOnly(1984, 7, 12)),
            Profile("4857773456", "Morgan", "Alex", new DateOnly(1984, 7, 12)),
            MatchGrade.Probable,
            approvalPolicyLocked: true,
            similarity: 1);

        var assessment = ReviewAssistant.Assess(detail);

        Assert.Equal("Pause and verify identifiers", assessment.Title);
        Assert.Equal("danger", assessment.Tone);
        Assert.Contains(
            assessment.Rows,
            row => row.Field == "NHS number" && row.State == ComparisonState.Conflict);
    }

    private static ReviewCaseDetail Detail(
        IdentityProfile subjectProfile,
        IdentityProfile candidateProfile,
        MatchGrade grade,
        bool approvalPolicyLocked,
        double similarity)
    {
        var now = DateTimeOffset.UtcNow;
        var subject = Patient(subjectProfile, now);
        var candidate = Patient(candidateProfile, now);
        var evidence = new[]
        {
            new FieldEvidence("family", similarity, 0.25, "test"),
            new FieldEvidence("given", similarity, 0.20, "test"),
            new FieldEvidence("birthDate", similarity, 0.30, "test"),
            new FieldEvidence("address", similarity, 0.15, "test"),
            new FieldEvidence("telecom", similarity, 0.07, "test"),
            new FieldEvidence("gender", similarity, 0.03, "test")
        };
        var review = new ReviewCase(
            Guid.CreateVersion7(),
            subject.EnterpriseId,
            candidate.EnterpriseId,
            similarity,
            grade,
            evidence,
            "test-profile",
            ReviewCaseStatus.Pending,
            now,
            now,
            1,
            ApprovalPolicyLocked: approvalPolicyLocked);
        return new ReviewCaseDetail(
            review,
            new PatientIdentityView(
                subject,
                new EnterprisePerson(subject.EnterpriseId, [], now, now, 1),
                []),
            new PatientIdentityView(
                candidate,
                new EnterprisePerson(candidate.EnterpriseId, [], now, now, 1),
                []),
            2);
    }

    private static CanonicalPatient Patient(
        IdentityProfile profile,
        DateTimeOffset now) =>
        new(EnterpriseId.New(), profile, [], [], 100, now, now, 1);

    private static IdentityProfile Profile(
        string nhsNumber,
        string family,
        string given,
        DateOnly birthDate) =>
        new(
            [new IdentityIdentifier(
                "https://fhir.nhs.uk/Id/nhs-number",
                nhsNumber,
                IsVerified: true,
                IsAuthoritative: true)],
            [new PersonName(family, [given], NameUse.Official)],
            birthDate,
            AdministrativeGender.Female,
            [new PostalAddress(["10 High Street"], "Cardiff", null, "CF10 1AA", "GB")],
            [new ContactPoint(ContactPointSystem.Phone, "07123456789")]);
}

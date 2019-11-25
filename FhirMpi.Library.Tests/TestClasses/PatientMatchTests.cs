using FhirMpi.Library.Helpers;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FhirMpi.Library.Tests.TestClasses
{
    [TestClass]
    public class PatientMatchTests : TestBase
    {
        private List<Patient> _testPatients;
        private const int TestPatientCount = 100000;

        static PatientMatchTests()
        {
            Console.WriteLine("PatientMatchTests.PatientMatchTests()");
        }

        [TestInitialize]
        public void TestInit()
        {
            _testPatients = new List<Patient>();
            for (var i = 0; i < TestPatientCount; i++)
            {
                _testPatients.Add(RandomHelper.GetRandomFhirPatient());
            }
            Console.WriteLine("PatientMatchTests.TestInit()");
        }

        [TestMethod]
        public void Should_Match_Patient_Certain()
        {
            // Arrange

            var patient = Random.NextChoice(_testPatients);
            var matches = new Bundle();

            // Act

            var patientNhsNumber = patient.GetNhsNumber();
            var patientFamilyName = patient.GetFamilyName();
            var patientGivenName = patient.GetGivenName();
            var patientDateOfBirth = patient.BirthDate;
            var patientGender = patient.Gender.ToString();
            var patientAddress = patient.Address.First().ToXml();

            foreach (var testPatient in _testPatients)
            {
                // 0. CHECK: NHS Number
                if (patientNhsNumber != null && testPatient.GetNhsNumber() != null && patientNhsNumber == testPatient.GetNhsNumber())
                {
                    matches.AddResourceEntry(testPatient, Guid.NewGuid().ToString());
                    matches.Entry.Last().AddExtension("http://hl7.org/fhir/StructureDefinition/match-grade", new CodeableConcept("MatchGrade", "certain", "Certain Match"));
                    continue;
                }

                // track score
                var score = 0.0;

                // 1. CHECK: Family Name
                if (patientFamilyName == testPatient.GetFamilyName())
                    score += 0.2;
                
                // 2. CHECK: Given Name
                if (patientGivenName == testPatient.GetGivenName())
                    score += 0.2;

                // REJECT-DECISION: these patients don't match
                if (score.IsEqualTo(0.0))
                    continue;
                
                // 3. CHECK: Date of Birth
                if (patientDateOfBirth == testPatient.BirthDate)
                    score += 0.2;

                // REJECT-DECISION: these patients don't match
                if (score.IsEqualTo(0.2))
                    continue;

                // ACCEPT-DECISION: these patients probably match
                if (score.IsEqualTo(0.6))
                {
                    matches.AddResourceEntry(testPatient, Guid.NewGuid().ToString());
                    matches.Entry.Last().AddExtension("http://hl7.org/fhir/StructureDefinition/match-grade", new CodeableConcept("MatchGrade", "probable", "Probable Match"));
                    continue;
                }

                // 4. CHECK: Gender
                if (patientGender == testPatient.Gender.ToString())
                    score += 0.2;

                // 5. CHECK: Address
                if (patientAddress == testPatient.Address.FirstOrDefault()?.ToXml())
                    score += 0.2;

                // REJECT-DECISION: these patients don't match
                if (score.IsEqualTo(0.4))
                    continue;

                // ACCEPT-DECISION: these patients probably match
                if (score.IsEqualTo(0.8))
                {
                    matches.AddResourceEntry(testPatient, Guid.NewGuid().ToString());
                    matches.Entry.Last().AddExtension("http://hl7.org/fhir/StructureDefinition/match-grade", new CodeableConcept("MatchGrade", "possible", "Possible Match"));
                }
            }

            matches.Entry = matches.Entry.OrderBy(x => 
                ((CodeableConcept)x.GetExtension("http://hl7.org/fhir/StructureDefinition/match-grade").Value).Coding.First().Code == "certain" ? 1:
                ((CodeableConcept)x.GetExtension("http://hl7.org/fhir/StructureDefinition/match-grade").Value).Coding.First().Code == "probable" ? 2:
                ((CodeableConcept)x.GetExtension("http://hl7.org/fhir/StructureDefinition/match-grade").Value).Coding.First().Code == "possible" ? 3: 4).ToList();

            Console.WriteLine($"searched for patient:\n{patient.ToXml()}");
            Console.WriteLine($"\nfound {matches.Entry.Count} matches in {_testPatients.Count} patients.\n");
            Console.WriteLine(matches.ToXml());
            Assert.IsTrue(matches.Entry.Count > 0);
        }

        [TestMethod]
        public void Should_Match_Patient_Probable()
        {
            // Arrange

            var patient = RandomHelper.GetRandomFhirPatient(); // create random patient
            patient.Identifier = new List<Identifier>(); // remove nhs number so we don't get certain match
            var plantedPatient = patient; // create a copy of the patient
            _testPatients[RandomHelper.GetRandomInteger(0, _testPatients.Count - 1)] = plantedPatient; // plant the copy in the test patients
            var matches = new Bundle();

            // Act
            
            var patientNhsNumber = patient.GetNhsNumber();
            var patientFamilyName = patient.GetFamilyName();
            var patientGivenName = patient.GetGivenName();
            var patientDateOfBirth = patient.BirthDate;
            var patientGender = patient.Gender.ToString();
            var patientAddress = patient.Address.First().ToXml();

            foreach (var testPatient in _testPatients)
            {
                // 0. CHECK: NHS Number
                if (patientNhsNumber != null && testPatient.GetNhsNumber() != null && patientNhsNumber == testPatient.GetNhsNumber())
                {
                    matches.AddResourceEntry(testPatient, Guid.NewGuid().ToString());
                    matches.Entry.Last().AddExtension("http://hl7.org/fhir/StructureDefinition/match-grade", new CodeableConcept("MatchGrade", "certain", "Certain Match"));
                    Console.WriteLine($"Successfully matched patient on NHS number.");
                    continue;
                }

                // track score
                var score = 0.0;

                // 1. CHECK: Family Name
                if (patientFamilyName == testPatient.GetFamilyName())
                    score += 0.2;

                // 2. CHECK: Given Name
                if (patientGivenName == testPatient.GetGivenName())
                    score += 0.2;

                // REJECT-DECISION: these patients don't match
                if (score.IsEqualTo(0.0))
                    continue;

                // 3. CHECK: Date of Birth
                if (patientDateOfBirth == testPatient.BirthDate)
                    score += 0.2;

                // REJECT-DECISION: these patients don't match
                if (score.IsEqualTo(0.2))
                    continue;

                // ACCEPT-DECISION: these patients probably match
                if (score.IsEqualTo(0.6))
                {
                    matches.AddResourceEntry(testPatient, Guid.NewGuid().ToString());
                    matches.Entry.Last().AddExtension("http://hl7.org/fhir/StructureDefinition/match-grade", new CodeableConcept("MatchGrade", "probable", "Probable Match"));
                    continue;
                }

                // 4. CHECK: Gender
                if (patientGender == testPatient.Gender.ToString())
                    score += 0.2;

                // 5. CHECK: Address
                if (patientAddress == testPatient.Address.FirstOrDefault()?.ToXml())
                    score += 0.2;

                // REJECT-DECISION: these patients don't match
                if (score.IsEqualTo(0.4))
                    continue;

                // ACCEPT-DECISION: these patients probably match
                if (score.IsEqualTo(0.8))
                {
                    matches.AddResourceEntry(testPatient, Guid.NewGuid().ToString());
                    matches.Entry.Last().AddExtension("http://hl7.org/fhir/StructureDefinition/match-grade", new CodeableConcept("MatchGrade", "possible", "Possible Match"));
                }
            }

            matches.Entry = matches.Entry.OrderBy(x =>
                ((CodeableConcept)x.GetExtension("http://hl7.org/fhir/StructureDefinition/match-grade").Value).Coding.First().Code == "certain" ? 1 :
                ((CodeableConcept)x.GetExtension("http://hl7.org/fhir/StructureDefinition/match-grade").Value).Coding.First().Code == "probable" ? 2 :
                ((CodeableConcept)x.GetExtension("http://hl7.org/fhir/StructureDefinition/match-grade").Value).Coding.First().Code == "possible" ? 3 : 4).ToList();

            Console.WriteLine($"searched for patient:\n{patient.ToXml()}");
            Console.WriteLine($"\nfound {matches.Entry.Count} matches in {_testPatients.Count} patients.\n");
            Console.WriteLine(matches.ToXml());
            Assert.IsTrue(matches.Entry.Count > 0);
        }

        [TestMethod]
        public void Should_Match_Patient_Possible()
        {
            // Arrange

            var patient = RandomHelper.GetRandomFhirPatient(); // create random patient
            patient.Identifier = new List<Identifier>(); // remove nhs number so we don't get certain match
            patient.Name.First().Family = string.Empty;
            patient.Address.First().PostalCode = string.Empty;
            patient.Address.First().Line = new List<string>();
            patient.BirthDate = string.Empty;
            var plantedPatient = patient; // create a copy of the patient
            _testPatients[RandomHelper.GetRandomInteger(0, _testPatients.Count - 1)] = plantedPatient; // plant the copy in the test patients
            var matches = new Bundle();

            // Act

            var patientNhsNumber = patient.GetNhsNumber();
            var patientFamilyName = patient.GetFamilyName();
            var patientGivenName = patient.GetGivenName();
            var patientDateOfBirth = patient.BirthDate;
            var patientGender = patient.Gender.ToString();
            var patientAddress = patient.Address.First().ToXml();

            foreach (var testPatient in _testPatients)
            {
                testPatient.Address.First().Line = new List<string>();
                testPatient.Address.First().PostalCode = string.Empty;
                testPatient.BirthDate = string.Empty;

                // 0. CHECK: NHS Number
                if (patientNhsNumber != null && testPatient.GetNhsNumber() != null && patientNhsNumber == testPatient.GetNhsNumber())
                {
                    matches.AddResourceEntry(testPatient, Guid.NewGuid().ToString());
                    matches.Entry.Last().AddExtension("http://hl7.org/fhir/StructureDefinition/match-grade", new CodeableConcept("MatchGrade", "certain", "Certain Match"));
                    Console.WriteLine($"Successfully matched patient on NHS number.");
                    continue;
                }

                // track score
                var score = 0.0;

                // 1. CHECK: Family Name
                if (patientFamilyName == testPatient.GetFamilyName())
                    score += 0.2;

                // 2. CHECK: Given Name
                if (patientGivenName == testPatient.GetGivenName())
                    score += 0.2;

                // REJECT-DECISION: these patients don't match
                if (score.IsEqualTo(0.0))
                    continue;

                // 3. CHECK: Date of Birth
                if (patientDateOfBirth == testPatient.BirthDate)
                    score += 0.2;

                // REJECT-DECISION: these patients don't match
                if (score.IsEqualTo(0.2))
                    continue;

                // ACCEPT-DECISION: these patients probably match
                if (score.IsEqualTo(0.6))
                {
                    matches.AddResourceEntry(testPatient, Guid.NewGuid().ToString());
                    matches.Entry.Last().AddExtension("http://hl7.org/fhir/StructureDefinition/match-grade", new CodeableConcept("MatchGrade", "probable", "Probable Match"));
                    continue;
                }

                // 4. CHECK: Gender
                if (patientGender == testPatient.Gender.ToString())
                    score += 0.2;

                // 5. CHECK: Address
                if (patientAddress == testPatient.Address.FirstOrDefault()?.ToXml())
                    score += 0.2;

                // REJECT-DECISION: these patients don't match
                if (score.IsEqualTo(0.4))
                    continue;

                // ACCEPT-DECISION: these patients probably match
                if (score.IsEqualTo(0.8))
                {
                    matches.AddResourceEntry(testPatient, Guid.NewGuid().ToString());
                    matches.Entry.Last().AddExtension("http://hl7.org/fhir/StructureDefinition/match-grade", new CodeableConcept("MatchGrade", "possible", "Possible Match"));
                }
            }

            matches.Entry = matches.Entry.OrderBy(x =>
                ((CodeableConcept)x.GetExtension("http://hl7.org/fhir/StructureDefinition/match-grade").Value).Coding.First().Code == "certain" ? 1 :
                ((CodeableConcept)x.GetExtension("http://hl7.org/fhir/StructureDefinition/match-grade").Value).Coding.First().Code == "probable" ? 2 :
                ((CodeableConcept)x.GetExtension("http://hl7.org/fhir/StructureDefinition/match-grade").Value).Coding.First().Code == "possible" ? 3 : 4).ToList();

            Console.WriteLine($"searched for patient:\n{patient.ToXml()}");
            Console.WriteLine($"\nfound {matches.Entry.Count} matches in {_testPatients.Count} patients.\n");
            Console.WriteLine(matches.ToXml());
            Assert.IsTrue(matches.Entry.Count > 0);
        }

        [TestMethod]
        public void Should_Match_Multiple_Varied()
        {
            // Arrange

            var certainPatient = RandomHelper.GetRandomFhirPatient();
            var probablePatient = new Patient();
            var possiblePatient = new Patient();

            certainPatient.CopyTo(probablePatient);
            certainPatient.CopyTo(possiblePatient);

            // remove nhs number so we don't get certain match
            probablePatient.Identifier = new List<Identifier>(); 
            possiblePatient.Identifier = new List<Identifier>();

            possiblePatient.Name.First().Family = string.Empty;

            _testPatients[RandomHelper.GetRandomInteger(0, _testPatients.Count - 1)] = certainPatient;
            _testPatients[RandomHelper.GetRandomInteger(0, _testPatients.Count - 1)] = probablePatient;
            _testPatients[RandomHelper.GetRandomInteger(0, _testPatients.Count - 1)] = possiblePatient;

            var matches = new Bundle();

            // Act

            var patientNhsNumber = certainPatient.GetNhsNumber();
            var patientFamilyName = certainPatient.GetFamilyName();
            var patientGivenName = certainPatient.GetGivenName();
            var patientDateOfBirth = certainPatient.BirthDate;
            var patientGender = certainPatient.Gender.ToString();
            var patientAddress = certainPatient.Address.First().ToXml();

            foreach (var testPatient in _testPatients)
            {
                // 0. CHECK: NHS Number
                if (patientNhsNumber != null && testPatient.GetNhsNumber() != null && patientNhsNumber == testPatient.GetNhsNumber())
                {
                    matches.AddResourceEntry(testPatient, Guid.NewGuid().ToString());
                    matches.Entry.Last().AddExtension("http://hl7.org/fhir/StructureDefinition/match-grade", new CodeableConcept("MatchGrade", "certain", "Certain Match"));
                    Console.WriteLine($"Successfully matched patient on NHS number.");
                    continue;
                }

                // track score
                var score = 0.0;

                // 1. CHECK: Family Name
                if (patientFamilyName == testPatient.GetFamilyName())
                    score += 0.2;

                // 2. CHECK: Given Name
                if (patientGivenName == testPatient.GetGivenName())
                    score += 0.2;

                // REJECT-DECISION: these patients don't match
                if (score.IsEqualTo(0.0))
                    continue;

                // 3. CHECK: Date of Birth
                if (patientDateOfBirth == testPatient.BirthDate)
                    score += 0.2;

                // REJECT-DECISION: these patients don't match
                if (score.IsEqualTo(0.2))
                    continue;

                // ACCEPT-DECISION: these patients probably match
                if (score.IsEqualTo(0.6))
                {
                    matches.AddResourceEntry(testPatient, Guid.NewGuid().ToString());
                    matches.Entry.Last().AddExtension("http://hl7.org/fhir/StructureDefinition/match-grade", new CodeableConcept("MatchGrade", "probable", "Probable Match"));
                    continue;
                }

                // 4. CHECK: Gender
                if (patientGender == testPatient.Gender.ToString())
                    score += 0.2;

                // 5. CHECK: Address
                if (patientAddress == testPatient.Address.FirstOrDefault()?.ToXml())
                    score += 0.2;

                // REJECT-DECISION: these patients don't match
                if (score.IsEqualTo(0.4))
                    continue;

                // ACCEPT-DECISION: these patients probably match
                if (score.IsEqualTo(0.8))
                {
                    matches.AddResourceEntry(testPatient, Guid.NewGuid().ToString());
                    matches.Entry.Last().AddExtension("http://hl7.org/fhir/StructureDefinition/match-grade", new CodeableConcept("MatchGrade", "possible", "Possible Match"));
                }
            }

            matches.Entry = matches.Entry.OrderBy(x =>
                ((CodeableConcept)x.GetExtension("http://hl7.org/fhir/StructureDefinition/match-grade").Value).Coding.First().Code == "certain" ? 1 :
                ((CodeableConcept)x.GetExtension("http://hl7.org/fhir/StructureDefinition/match-grade").Value).Coding.First().Code == "probable" ? 2 :
                ((CodeableConcept)x.GetExtension("http://hl7.org/fhir/StructureDefinition/match-grade").Value).Coding.First().Code == "possible" ? 3 : 4).ToList();

            Console.WriteLine($"searched for patient:\n{certainPatient.ToXml()}");
            Console.WriteLine($"\nfound {matches.Entry.Count} matches in {_testPatients.Count} patients.\n");
            Console.WriteLine(matches.ToXml());
            Assert.IsTrue(matches.Entry.Count > 0);
        }
    }
}
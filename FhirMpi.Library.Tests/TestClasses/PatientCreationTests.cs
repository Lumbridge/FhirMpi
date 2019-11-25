using System;
using System.Collections.Generic;
using FhirMpi.Library.Helpers;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FhirMpi.Library.Tests.TestClasses
{
    [TestClass]
    public class PatientCreationTests : TestBase
    {
        [TestMethod]
        public void Should_Create_Fhir_Test_Patient()
        {
            var patient = new Patient
            {
                Identifier = new List<Identifier>
                {
                    new Identifier("NHS", RandomHelper.GetRandomNumberOfFixedLength(10))
                },
                Name = new List<HumanName>
                {
                    RandomHelper.GetRandomHumanName()
                },
                BirthDate = RandomHelper.GetRandomFhirDate(new DateTime(1985, 1, 1), DateTime.Now).ToString(),
                Address = new List<Address>
                {
                    RandomHelper.GetRandomFhirAddress()
                },
                Gender = RandomHelper.GetRandomFhirGender()
            };
            Console.WriteLine($"Generated patient {patient.ToXml()}");
        }
    }
}

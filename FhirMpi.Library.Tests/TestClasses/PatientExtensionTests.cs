using System;
using System.Linq;
using FhirMpi.Library.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FhirMpi.Library.Tests.TestClasses
{
    [TestClass]
    public class PatientExtensionTests : TestBase
    {
        [TestMethod]
        public void Should_Get_Patient_Given_Name()
        {
            // Arrange
            var patient = RandomHelper.GetRandomFhirPatient();

            // Act
            var patientName = patient.Name.FirstOrDefault();
            if (patientName == null)
            {
                throw new Exception("Patient doesn't have a Name element.");
            }

            var givenName = patientName.GivenElement.FirstOrDefault();
            if (givenName == null)
            {
                throw new Exception("Patient doesn't have a GivenName element.");
            }

            // Assert
            Console.WriteLine($"Patient has GivenName: {givenName}");
            Assert.IsNotNull(givenName);
            Assert.IsTrue(Constants.Colours.Contains(givenName.Value));
        }

        [TestMethod]
        public void Should_Get_Patient_Family_Name()
        {
            // Arrange
            var patient = RandomHelper.GetRandomFhirPatient();

            // Act
            var patientName = patient.Name.FirstOrDefault();
            if (patientName == null)
            {
                throw new Exception("Patient doesn't have a Name element.");
            }

            var familyName = patientName.FamilyElement;
            if (familyName == null)
            {
                throw new Exception("Patient doesn't have a GivenName element.");
            }

            // Assert
            Console.WriteLine($"Patient has FamilyName: {familyName}");
            Assert.IsNotNull(familyName);
            Assert.IsTrue(Constants.Animals.Contains(familyName.Value));
        }
    }
}

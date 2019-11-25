using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using FhirMpi.Library.Helpers;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FhirMpi.Library.Tests.TestClasses
{
    [TestClass]
    public class RandomAttributeTests : TestBase
    {
        [TestMethod]
        public void Should_Generate_Integer_Between_Range()
        {
            var highestResult = int.MinValue;
            var lowestResult = int.MaxValue;

            const int lowerRange = 5;
            const int upperRange = 10;

            Console.WriteLine($"random int test\nlower range = {lowerRange}, upper range = {upperRange}.");

            var success = true;
            for (var i = 0; i < CycleCount; i++)
            {
                lock (Random)
                {
                    var result = Random.Next(lowerRange, upperRange + 1);
                    if (result < lowestResult)
                        lowestResult = result;
                    if (result > highestResult)
                        highestResult = result;
                    if (result < lowerRange || result > upperRange)
                    {
                        success = false;
                        break;
                    }
                }
            }

            Console.WriteLine($"ran {CycleCount} cycles, lowest result was {lowestResult}, highest result was {highestResult}.");

            Assert.IsTrue(success);
        }

        [TestMethod]
        public void Should_Generate_Integer_Of_Fixed_Length()
        {
            const int length = 10;
            var s = string.Empty;
            for (var i = 0; i < length; i++)
                s = string.Concat(s, RandomHelper.GetRandomInteger(0, 9).ToString());
            Console.WriteLine($"generated random number of fixed length ({length}): {s}.");
            Assert.AreEqual(10, s.Length);
        }

        [TestMethod]
        public void Should_Generate_Random_Date_Between_Range()
        {
            var highestResult = DateTime.MinValue;
            var lowestResult = DateTime.MaxValue;

            var lowerRange = new DateTime(1985, 1, 1);
            var upperRange = DateTime.Now.Subtract(new TimeSpan(365, 0, 0, 0));

            Console.WriteLine($"random date test\nlower range = {lowerRange}, upper range = {upperRange}.");

            var success = true;
            for (var i = 0; i < CycleCount; i++)
            {
                var range = (upperRange - lowerRange).Days;
                lock (Random)
                {
                    var result = lowerRange.AddDays(Random.Next(range));
                    if (result < lowestResult)
                        lowestResult = result;
                    if (result > highestResult)
                        highestResult = result;
                    if (result < lowerRange || result > upperRange)
                    {
                        success = false;
                        break;
                    }
                }
            }

            Console.WriteLine($"ran {CycleCount} cycles, lowest result was {lowestResult.ToShortDateString()}, highest result was {highestResult.ToShortDateString()}.");

            Assert.IsTrue(success);
        }

        [TestMethod]
        public void Should_Generate_Random_Fhir_Date_Between_Range()
        {
            var highestResult = DateTimeOffset.MinValue;
            var lowestResult = DateTimeOffset.MaxValue;

            var lowerRange = new DateTimeOffset(new DateTime(1985, 1, 1));
            var upperRange = new DateTimeOffset(DateTime.Now);

            Console.WriteLine($"random date test\nlower range = {lowerRange.ToString()}, upper range = {upperRange.ToString()}.");

            var success = true;
            for (var i = 0; i < CycleCount; i++)
            {
                var range = (upperRange - lowerRange).Days;
                lock (Random)
                {
                    DateTimeOffset result = lowerRange.AddDays(Random.Next(range));
                    if (result < lowestResult)
                        lowestResult = result;
                    if (result > highestResult)
                        highestResult = result;
                    if (result < lowerRange || result > upperRange)
                    {
                        success = false;
                        break;
                    }
                }
            }

            Console.WriteLine($"ran {CycleCount} cycles, lowest result was {new FhirDateTime(lowestResult)}, highest result was {new FhirDateTime(highestResult)}.");

            Assert.IsTrue(success);
        }

        [TestMethod]
        public void Should_Generate_Random_Fhir_Gender()
        {
            var generatedMale = false;
            var generatedFemale = false;
            var generatedOther = false;
            var generatedUnknown = false;

            var gender = AdministrativeGender.Other;
            int count = 0;
            while (!generatedMale || !generatedFemale || !generatedOther || !generatedUnknown)
            {
                gender = (AdministrativeGender) RandomHelper.GetRandomInteger(0, 3);
                ++count;
                switch (gender)
                {
                    case AdministrativeGender.Male:
                        generatedMale = true;
                        break;
                    case AdministrativeGender.Female:
                        generatedFemale = true;
                        break;
                    case AdministrativeGender.Other:
                        generatedOther = true;
                        break;
                    case AdministrativeGender.Unknown:
                        generatedUnknown = true;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            Console.WriteLine($"generated all gender types in {count} cycles.");
            Assert.IsTrue(gender == AdministrativeGender.Female ||
                          gender == AdministrativeGender.Male ||
                          gender == AdministrativeGender.Other ||
                          gender == AdministrativeGender.Unknown);
        }

        [TestMethod]
        public void Should_Generate_Random_Nhs_Number()
        {
            var nhsNumber = RandomHelper.GetRandomNumberOfFixedLength(10);
            Console.WriteLine($"generated nhs number: {nhsNumber} (length of {nhsNumber.Length}).");
            Assert.AreEqual(10, nhsNumber.Length);
        }

        [TestMethod]
        public void Should_Generate_Random_Post_Code()
        {
            for (var i = 0; i < CycleCount; i++)
            {
                var postCode = Random.NextString(Constants.CHARS_ALPHA_UPPER, 2, 2);
                postCode += Random.NextString(Constants.CHARS_NUM, 1, 2);
                postCode += " ";
                postCode += Random.NextString(Constants.CHARS_NUM, 1, 1);
                postCode += Random.NextString(Constants.CHARS_ALPHA_UPPER, 2, 2);
                if (!Regex.IsMatch(postCode, @"(GIR 0AA)|((([A-Z][0-9][0-9]?)|(([A-Z][A-Z][0-9][0-9]?)|(([A-Z][0-9])|([A-Z][A-Z][0-9])))) [0-9][A-Z]{2})"))
                    throw new Exception($"Generated Postcode: {postCode} failed regex matching");
                Console.WriteLine($"Generated random postcode: {postCode}.");
            }
        }

        [TestMethod]
        public void Should_Generate_Random_Fhir_Address()
        {
            for (var i = 0; i < CycleCount; i++)
            {
                var address = new Address
                {
                    Line = new List<string> {
                        Random.NextChoice(Constants.StreetNames),
                        RandomHelper.GetRandomInteger(1,499).ToString()
                    },
                    City = Random.NextChoice(Constants.Cities),
                    Country = "Wales",
                    PostalCode = RandomHelper.GetRandomPostCode(),
                    
                };
                Console.WriteLine($"Generated address {address.ToXml()}");
            }
        }

        [TestMethod]
        public void Should_Generate_Random_Names()
        {
            for (var i = 0; i < CycleCount; i++)
            {
                var givenName = Random.NextChoice(Constants.Colours);
                var familyName = Random.NextChoice(Constants.Animals);
                Console.WriteLine($"Generated name: \"{givenName} {familyName}\"");
            }
        }
    }
}

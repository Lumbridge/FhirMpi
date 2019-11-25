using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Hl7.Fhir.Model;

namespace FhirMpi.Library.Helpers
{
    public static class PatientExtension
    {
        public static string GetGivenName(this Patient patient)
        {
            // get patient name element
            var patientName = patient.Name.FirstOrDefault();
            // try get the given name from the name element
            var givenName = patientName?.GivenElement.FirstOrDefault();
            // return the given name or an empty string
            return givenName == null ? string.Empty : givenName.Value;
        }

        public static string GetFamilyName(this Patient patient)
        {
            // get patient name element
            var patientName = patient.Name.FirstOrDefault();
            // try get the given name from the name element
            var familyName = patientName?.FamilyElement;
            // return the given name or an empty string
            return familyName == null ? string.Empty : familyName.Value;
        }

        public static Identifier GetNhsNumber(this Patient patient)
        {
            return patient.Identifier.FirstOrDefault(x => x.System == "NHS");
        }
    }
}

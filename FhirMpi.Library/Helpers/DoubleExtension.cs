using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FhirMpi.Library.Helpers
{
    public static class DoubleExtension
    {
        public static bool IsEqualTo(this double number, double compareWith)
        {
            return Math.Abs(compareWith - number) < 1E-9;
        }
    }
}

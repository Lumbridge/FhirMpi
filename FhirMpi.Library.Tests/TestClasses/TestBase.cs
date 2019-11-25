using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FhirMpi.Library.Tests.TestClasses
{
    public class TestBase
    {
        public Random Random { get; set; }
        public const int CycleCount = 150;

        static TestBase()
        {
            Console.WriteLine("TestBase Created.");
        }

        [TestInitialize]
        public void BaseTestInit()
        {
            Console.WriteLine("TestBase Initialised.");

            // init random object for random tests
            Random = new Random(Guid.NewGuid().GetHashCode());
        }

        [TestCleanup]
        public void BaseTestCleanup()
        {
        }
    }
}

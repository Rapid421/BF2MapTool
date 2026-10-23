using System;
using FrostyCLI.Core;

namespace FrostyCLI.Tests
{
    public class TypeCoercionTests
    {
        [Test]
        public void Coerce_String_ReturnsString()
        {
            var result = TypeCoercion.Coerce(typeof(string), "hello");
            Assert.AreEqual("hello", result);
        }

        [Test]
        public void Coerce_Bool_True()
        {
            Assert.AreEqual(true, TypeCoercion.Coerce(typeof(bool), "True"));
        }

        [Test]
        public void Coerce_Bool_False()
        {
            Assert.AreEqual(false, TypeCoercion.Coerce(typeof(bool), "False"));
        }

        [Test]
        public void Coerce_Int_ParsesInvariant()
        {
            Assert.AreEqual(42, TypeCoercion.Coerce(typeof(int), "42"));
        }

        [Test]
        public void Coerce_UInt_Parses()
        {
            Assert.AreEqual(uint.MaxValue, TypeCoercion.Coerce(typeof(uint), "4294967295"));
        }

        [Test]
        public void Coerce_Long_Parses()
        {
            Assert.AreEqual(long.MinValue, TypeCoercion.Coerce(typeof(long), "-9223372036854775808"));
        }

        [Test]
        public void Coerce_ULong_DecimalParses()
        {
            Assert.AreEqual(ulong.MaxValue, TypeCoercion.Coerce(typeof(ulong), "18446744073709551615"));
        }

        [Test]
        public void Coerce_ULong_HexParses()
        {
            Assert.AreEqual(0xDEADBEEFUL, TypeCoercion.Coerce(typeof(ulong), "0xDEADBEEF"));
        }

        [Test]
        public void Coerce_Float_InvariantCulture()
        {
            var result = (float)TypeCoercion.Coerce(typeof(float), "3.14");
            Assert.IsTrue(Math.Abs(result - 3.14f) < 0.0001f);
        }

        [Test]
        public void Coerce_Double_InvariantCulture()
        {
            var result = (double)TypeCoercion.Coerce(typeof(double), "2.718281828");
            Assert.IsTrue(Math.Abs(result - 2.718281828) < 1e-9);
        }

        [Test]
        public void Coerce_Guid_Parses()
        {
            string guidStr = "12345678-1234-1234-1234-123456789abc";
            Assert.AreEqual(Guid.Parse(guidStr), TypeCoercion.Coerce(typeof(Guid), guidStr));
        }

        [Test]
        public void Coerce_Enum_CaseInsensitive()
        {
            Assert.AreEqual(DayOfWeek.Monday, TypeCoercion.Coerce(typeof(DayOfWeek), "monday"));
        }

        [Test]
        public void Coerce_UnsupportedType_ThrowsInvalidOperationException()
        {
            Assert.Throws<InvalidOperationException>(
                () => TypeCoercion.Coerce(typeof(object), "value"));
        }

        [Test]
        public void Coerce_NullTargetType_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(
                () => TypeCoercion.Coerce(null, "value"));
        }

        [Test]
        public void Coerce_Short_Parses()
        {
            Assert.AreEqual(short.MinValue, TypeCoercion.Coerce(typeof(short), "-32768"));
        }

        [Test]
        public void Coerce_UShort_Parses()
        {
            Assert.AreEqual(ushort.MaxValue, TypeCoercion.Coerce(typeof(ushort), "65535"));
        }

        [Test]
        public void Coerce_Byte_Parses()
        {
            Assert.AreEqual((byte)255, TypeCoercion.Coerce(typeof(byte), "255"));
        }
    }
}

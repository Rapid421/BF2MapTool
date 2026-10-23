using System;
namespace FrostyCLI.Tests
{
    public static class Assert
    {
        public static void AreEqual(object expected, object actual)
        {
            if (!Equals(expected, actual))
                throw new Exception("Expected <" + expected + "> but got <" + actual + ">");
        }
        public static void AreEqual(double expected, double actual, double tolerance)
        {
            if (Math.Abs(expected - actual) > tolerance)
                throw new Exception("Expected <" + expected + "> +/- " + tolerance + " but got <" + actual + ">");
        }
        public static void IsTrue(bool condition, string msg = null)
        {
            if (!condition) throw new Exception(msg ?? "Expected true");
        }
        public static void IsFalse(bool condition, string msg = null)
        {
            if (condition) throw new Exception(msg ?? "Expected false");
        }
        public static void IsNull(object value)
        {
            if (value != null) throw new Exception("Expected null but got " + value);
        }
        public static void IsNotNull(object value)
        {
            if (value == null) throw new Exception("Expected non-null value");
        }
        public static void Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            catch (Exception ex)
            {
                throw new Exception("Expected " + typeof(T).Name + " but got " + ex.GetType().Name + ": " + ex.Message);
            }
            throw new Exception("Expected " + typeof(T).Name + " but no exception was thrown");
        }
    }
}

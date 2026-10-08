using System;
using AIUsageBar.Core;
using Xunit;

namespace AIUsageBar.Tests
{
    public class JsonTests
    {
        [Fact]
        public void Deep_Nesting_Is_Rejected_Instead_Of_Overflowing_The_Stack()
        {
            Assert.Throws<FormatException>(() => Json.Parse(new string('[', 100000)));
            Assert.Throws<FormatException>(() => Json.Parse(new string('{', 1) + string.Concat(System.Linq.Enumerable.Repeat("\"a\":{", 5000))));
            Assert.NotNull(Json.Parse(new string('[', 50) + new string(']', 50)));
        }

        [Fact]
        public void Parse_Object_Nested()
        {
            var r = Json.Parse("{\"a\":{\"b\":[1,{\"c\":\"x\"}]},\"t\":true,\"n\":null}");
            Assert.Equal("x", Json.Get(r, "a.b.1.c"));
            Assert.Equal(1.0, Json.Get(r, "a.b.0"));
            Assert.Equal(true, Json.Get(r, "t"));
            Assert.Null(Json.Get(r, "n"));
            Assert.Null(Json.Get(r, "zz.y"));
        }

        [Fact]
        public void Parse_StringEscapes()
        {
            Assert.Equal("é\n\"", Json.Parse("\"\u00e9\n\\\"\""));
        }

        [Fact]
        public void Parse_Numbers()
        {
            Assert.Equal(-150.0, Json.Parse("-1.5e2"));
            Assert.Equal(62500.0, Json.Num(Json.Parse("\"62500\"")));
        }

        [Fact]
        public void Parse_Invalid_Throws()
        {
            Assert.Throws<FormatException>(() => Json.Parse("{\"a\":"));
        }
    }
}

﻿using System;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using GraphView;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphViewUnitTest.Gremlin.ProcessTests.Traversal.Step.Map
{
    [TestClass]
    public class GroupTest : AbstractGremlinTest
    {
        [TestMethod]
        [TestModernCompatible]
        public void g_V_Group()
        {
            using (GraphViewCommand GraphViewCommand = new GraphViewCommand(graphConnection))
            {
                GraphViewCommand.OutputFormat = OutputFormat.Regular;
                var traversal = GraphViewCommand.g().V().Group();
                var results = traversal.Next();

                foreach (var result in results)
                {
                    Console.WriteLine(result);
                }
            }
        }

        [TestMethod]
        [TestModernCompatible]
        public void g_V_Group_by()
        {
            using (GraphViewCommand GraphViewCommand = new GraphViewCommand(graphConnection))
            {
                GraphViewCommand.OutputFormat = OutputFormat.Regular;
                var traversal = GraphViewCommand.g().V().Group().By(GraphTraversal.__().Values("name")).By();
                var results = traversal.Next();

                Console.WriteLine(traversal.SqlScript);
                foreach (var result in results)
                {
                    Console.WriteLine(result);
                }
            }
        }

        [TestMethod]
        [TestModernCompatible]
        public void g_V_Group_by_select()
        {
            using (GraphViewCommand GraphViewCommand = new GraphViewCommand(graphConnection))
            {
                GraphViewCommand.OutputFormat = OutputFormat.Regular;
                var traversal = GraphViewCommand.g().V().As("a").In().Select("a").GroupCount().Unfold().Select(GremlinKeyword.Column.Keys).Out().ValueMap();
                var results = traversal.Next();

                Console.WriteLine(traversal.SqlScript);
                foreach (var result in results)
                {
                    Console.WriteLine(result);
                }
            }
        }

        [TestMethod]
        [TestModernCompatible()]
        public void g_V_GroupCount()
        {
            using (GraphViewCommand GraphViewCommand = new GraphViewCommand(graphConnection))
            {
                var traversal = GraphViewCommand.g().V().GroupCount().Order(GremlinKeyword.Scope.Local).By(GremlinKeyword.Column.Values, GremlinKeyword.Order.Decr);
                var result = traversal.Next();
                foreach (var r in result)
                {
                    Console.WriteLine(r);
                }
            }
        }
    }
}
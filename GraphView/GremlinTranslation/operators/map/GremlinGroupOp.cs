﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GraphView
{
    internal class GremlinGroupOp: GremlinTranslationOperator
    {
        public string SideEffect { get; set; }
        public GraphTraversal GroupBy { get; set; }
        public GraphTraversal ProjectBy { get; set; }
        public bool IsProjectingACollection { get; set; }

        public GremlinGroupOp()
        {
            IsProjectingACollection = true;
        }

        public GremlinGroupOp(string sideEffect)
        {
            SideEffect = sideEffect;
            IsProjectingACollection = true;
        }

        internal override GremlinToSqlContext GetContext()
        {
            GremlinToSqlContext inputContext = GetInputContext();
            if (inputContext.PivotVariable == null)
            {
                throw new TranslationException("The PivotVariable of group()-step can't be null.");
            }

            if (GroupBy == null)
                GroupBy = GraphTraversal.__();
            if (ProjectBy == null)
                ProjectBy = GraphTraversal.__();

            GroupBy.GetStartOp().InheritedVariableFromParent(inputContext);
            ProjectBy.GetStartOp().InheritedVariableFromParent(inputContext);
            GremlinToSqlContext groupByContext = GroupBy.GetEndOp().GetContext();
            GremlinToSqlContext projectContext = ProjectBy.GetEndOp().GetContext();

            inputContext.PivotVariable.Group(inputContext, SideEffect, groupByContext, projectContext, IsProjectingACollection);

            return inputContext;
        }

        public override void ModulateBy()
        {
            if (GroupBy == null)
            {
                GroupBy = GraphTraversal.__();
            }
            else if (ProjectBy == null)
            {
                ProjectBy = GraphTraversal.__();
            }
            else
            {
                throw new QueryCompilationException("The key and value traversals for group()-step have already been set");
            }
        }

        public override void ModulateBy(GraphTraversal traversal)
        {
            if (GroupBy == null)
            {
                GroupBy = traversal;
            }
            else if (ProjectBy == null)
            {
                ProjectBy = traversal;
                IsProjectingACollection = false;
            }
            else
            {
                throw new QueryCompilationException("The key and value traversals for group()-step have already been set");
            }
        }

        public override void ModulateBy(string key)
        {
            if (GroupBy == null)
            {
                GroupBy = GraphTraversal.__().Values(key);
            }
            else if (ProjectBy == null)
            {
                ProjectBy = GraphTraversal.__().Values(key);
            }
            else
            {
                throw new QueryCompilationException("The key and value traversals for group()-step have already been set");
            }
        }

        public override void ModulateBy(GremlinKeyword.Column column)
        {
            if (GroupBy == null)
            {
                GroupBy = GraphTraversal.__().Select(column);
            }
            else if (ProjectBy == null)
            {
                ProjectBy = GraphTraversal.__().Select(column);
            }
            else
            {
                throw new QueryCompilationException("The key and value traversals for group()-step have already been set");
            }
        }
    }
}

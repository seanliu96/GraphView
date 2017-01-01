﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GraphView
{
    internal class GremlinOptionalVariable : GremlinTableVariable, ISqlTable
    {
        private GremlinToSqlContext context;

        public GremlinOptionalVariable(GremlinToSqlContext context)
        {
            this.context = context;
            VariableName = GenerateTableAlias();
        }
        public List<WSelectElement> ToSelectElementList()
        {
            return null;
        }

        public WTableReference ToTableReference()
        {
            List<WScalarExpression> PropertyKeys = new List<WScalarExpression>();
            PropertyKeys.Add(GremlinUtil.GetScalarSubquery(context.ToSelectQueryBlock()));
            var secondTableRef = GremlinUtil.GetSchemaObjectFunctionTableReference("optional", PropertyKeys);
            secondTableRef.Alias = GremlinUtil.GetIdentifier(VariableName);
            return GremlinUtil.GetCrossApplyTableReference(null, secondTableRef);
        }
    }
}
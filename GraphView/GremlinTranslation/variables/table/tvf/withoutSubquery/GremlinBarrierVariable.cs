﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GraphView
{
    internal class GremlinBarrierVariable : GremlinTableVariable
    {
        public GremlinBarrierVariable(GremlinVariable inputVariable) : base(inputVariable.GetVariableType()) { }

        public override WTableReference ToTableReference()
        {
            var tableRef = SqlUtil.GetFunctionTableReference(GremlinKeyword.func.Barrier, new List<WScalarExpression>(), GetVariableName());
            return SqlUtil.GetCrossApplyTableReference(tableRef);
        }
    }
}

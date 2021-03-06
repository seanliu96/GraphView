﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GraphView
{
    internal class GremlinKeyVariable : GremlinScalarTableVariable
    {
        public GremlinVariable ProjectVariable { get; set; }

        public GremlinKeyVariable(GremlinVariable projectVariable)
        {
            GremlinVariableType inputVariableType = projectVariable.GetVariableType();
            if (!(GremlinVariableType.NULL <= inputVariableType && inputVariableType <= GremlinVariableType.Unknown ||
                  inputVariableType == GremlinVariableType.VertexProperty || inputVariableType == GremlinVariableType.Property))
            {
                throw new SyntaxErrorException("The inputVariable of key() can not be " + inputVariableType);
            }

            this.ProjectVariable = projectVariable;
        }

        internal override List<GremlinVariable> FetchAllVars()
        {
            List<GremlinVariable> variableList = new List<GremlinVariable>() { this };
            variableList.AddRange(this.ProjectVariable.FetchAllVars());
            return variableList;
        }

        public override WTableReference ToTableReference()
        {
            List<WScalarExpression> parameters = new List<WScalarExpression>();
            parameters.Add(this.ProjectVariable.DefaultProjection().ToScalarExpression());
            var tableRef = SqlUtil.GetFunctionTableReference(GremlinKeyword.func.Key, parameters, GetVariableName());
            return SqlUtil.GetCrossApplyTableReference(tableRef);
        }
    }
}

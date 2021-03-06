﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GraphView
{
    internal class GremlinMaxOp: GremlinTranslationOperator
    {
        public GremlinKeyword.Scope Scope { get; set; }

        public GremlinMaxOp(GremlinKeyword.Scope scope)
        {
            Scope = scope;
        }

        internal override GremlinToSqlContext GetContext()
        {
            GremlinToSqlContext inputContext = GetInputContext();
            if (inputContext.PivotVariable == null)
            {
                throw new TranslationException("The PivotVariable of max()-step can't be null.");
            }

            if (Scope == GremlinKeyword.Scope.Global)
            {
                inputContext.PivotVariable.Max(inputContext);
            }
            else
            {
                inputContext.PivotVariable.MaxLocal(inputContext);
            }
            return inputContext;
        }
    }
}

﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GraphView
{
    internal class GremlinOutEOp: GremlinTranslationOperator
    {
        internal List<string> EdgeLabels { get; set; }

        public GremlinOutEOp(params string[] labels)
        {
            EdgeLabels = new List<string>();
            foreach (var label in labels)
            {
                EdgeLabels.Add(label);
            }
        }

        internal override GremlinToSqlContext GetContext()
        {
            GremlinToSqlContext inputContext = GetInputContext();
            if (inputContext.PivotVariable == null)
            {
                throw new TranslationException("The PivotVariable of outE()-step can't be null.");
            }

            inputContext.PivotVariable.OutE(inputContext, EdgeLabels);

            return inputContext;
        }
    }
}

﻿// GraphView
// 
// Copyright (c) 2015 Microsoft Corporation
// 
// All rights reserved. 
// 
// MIT License
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of
// this software and associated documentation files (the "Software"), to deal in
// the Software without restriction, including without limitation the rights to
// use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
// the Software, and to permit persons to whom the Software is furnished to do so,
// subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
// FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
// COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
// IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
// CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// 
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

namespace GraphView
{
    internal class WColumnReferenceExpressionComparer : IEqualityComparer<WColumnReferenceExpression>
    {
        public bool Equals(WColumnReferenceExpression x, WColumnReferenceExpression y)
        {
            if (Object.ReferenceEquals(x, y)) return true;
            if (Object.ReferenceEquals(x, null) || Object.ReferenceEquals(y, null)) return false;
            return x.ToString().Equals(y.ToString());
        }

        public int GetHashCode(WColumnReferenceExpression obj)
        {
            if (Object.ReferenceEquals(obj, null)) return 0;
            return obj.ToString().GetHashCode();
        }
    }


    /// <summary>
    /// TemporaryTableHeader defines the columns of a temporary table
    /// </summary>
    internal class TemporaryTableHeader
    {
        // A map from column names to their offsets in raw records and their types
        public Dictionary<string, Tuple<int, ColumnGraphType>> columnSet { get; private set; }

        public TemporaryTableHeader()
        {
            columnSet = new Dictionary<string, Tuple<int, ColumnGraphType>>();
        }

        public TemporaryTableHeader(List<Tuple<string, ColumnGraphType>> columnList)
        {
            columnSet = new Dictionary<string, Tuple<int, ColumnGraphType>>(columnList.Count);
            
            for (int i = 0; i < columnList.Count; i++)
            {
                columnSet[columnList[i].Item1] = new Tuple<int, ColumnGraphType>(i, columnList[i].Item2);
            }
        }

        public void AddColumn(string columnName, ColumnGraphType ptype, int index)
        {
            // If the same column name has appeared before, the newly defined column
            // will override the older one and the older one will not be accessible.
            columnSet[columnName] = new Tuple<int, ColumnGraphType>(index, ptype);
        }

        public int GetColumnIndex(string columnName)
        {
            return columnSet.ContainsKey(columnName) ? columnSet[columnName].Item1 : -1;
        }
    }

    /// <summary>
    /// QueryCompilationContext is an entity providing contexts 
    /// for translating a SQL statement or a nested SQL query. 
    /// The context information includes temporary tables defined so far in the script 
    /// and the layout of raw records produced by the execution of the SQL statement/query.
    /// </summary>
    internal class QueryCompilationContext
    {
        // A collection of temporary tables defined in the script.
        // A temporary table has a table name, a table header defining column names and their types 
        // and an execution operator producing the records in the table 
        public Dictionary<string, Tuple<TemporaryTableHeader, GraphViewExecutionOperator>> TemporaryTableCollection { get; private set; }

        /// <summary>
        /// The layout of the raw records produced by the execution of the current query.
        /// A raw record is a collection of fields, each having a composite name in a pair of (table alias, column name)
        /// and pointing to the field's offset in the record and the field's type. 
        /// </summary>
        public Dictionary<WColumnReferenceExpression, int> RawRecordLayout { get; private set; }

        public GraphViewExecutionOperator CurrentExecutionOperator { get; set; }

        public EnumeratorOperator OuterContextOp { get; set; }

        public HashSet<string> TableReferences { get; private set; }

        public bool InBatchMode { get; set; }

        public bool CarryOn { get; set; }
        public Dictionary<WColumnReferenceExpression, int> ParentContextRawRecordLayout { get; private set; }

        public Dictionary<string, AggregateState> SideEffectStates { get; private set; }
        public Dictionary<string, IAggregateFunction> SideEffectFunctions { get; private set; }
        public ExecutionOrder CurrentExecutionOrder { get; set; }
        public List<ExecutionOrder> LocalExecutionOrders { get; set; }
        public List<Container> Containers { get; set; }


        public QueryCompilationContext()
        {
            TemporaryTableCollection = new Dictionary<string, Tuple<TemporaryTableHeader, GraphViewExecutionOperator>>();
            RawRecordLayout = new Dictionary<WColumnReferenceExpression, int>(new WColumnReferenceExpressionComparer());
            TableReferences = new HashSet<string>();
            SideEffectStates = new Dictionary<string, AggregateState>();
            SideEffectFunctions = new Dictionary<string, IAggregateFunction>();
            CarryOn = false;
            Containers = new List<Container>();
            CurrentExecutionOrder = new ExecutionOrder();
            LocalExecutionOrders = new List<ExecutionOrder>();
        }

        public QueryCompilationContext(QueryCompilationContext parentContext)
        {
            CurrentExecutionOperator = parentContext.CurrentExecutionOperator;
            TemporaryTableCollection = parentContext.TemporaryTableCollection;
            RawRecordLayout = new Dictionary<WColumnReferenceExpression, int>(parentContext.RawRecordLayout,
                new WColumnReferenceExpressionComparer());
            TableReferences = new HashSet<string>(parentContext.TableReferences);
            OuterContextOp = new EnumeratorOperator();
            CarryOn = false;
            ParentContextRawRecordLayout = new Dictionary<WColumnReferenceExpression, int>(
                parentContext.RawRecordLayout, new WColumnReferenceExpressionComparer());
            SideEffectStates = parentContext.SideEffectStates;
            SideEffectFunctions = parentContext.SideEffectFunctions;
            Containers = parentContext.Containers;
            CurrentExecutionOrder = new ExecutionOrder(parentContext.CurrentExecutionOrder);
            LocalExecutionOrders = new List<ExecutionOrder>();
        }

        public QueryCompilationContext(
            Dictionary<string, Tuple<TemporaryTableHeader, GraphViewExecutionOperator>> priorTemporaryTables,
            Dictionary<string, IAggregateFunction> priorSideEffectFunctions,
            Dictionary<string, AggregateState> priorSideEffectStates,
            List<Container> priorContainers)
        {
            TemporaryTableCollection = priorTemporaryTables;
            RawRecordLayout = new Dictionary<WColumnReferenceExpression, int>(new WColumnReferenceExpressionComparer());
            TableReferences = new HashSet<string>();
            SideEffectFunctions = priorSideEffectFunctions;
            SideEffectStates = priorSideEffectStates;
            Containers = priorContainers;
            CurrentExecutionOrder = new ExecutionOrder();
            LocalExecutionOrders = new List<ExecutionOrder>();
        }

        public int AddContainers(Container container)
        {
            this.Containers.Add(container);
            return Containers.Count-1;
        }

        /// <summary>
        /// Adds a new field to the raw records when a new execution operator is added to the execution plan.
        /// </summary>
        /// <param name="tableAlias"></param>
        /// <param name="columnName"></param>
        /// <param name="type"></param>
        /// <param name="insertAtFront"></param>
        public void AddField(string tableAlias, string columnName, ColumnGraphType type, bool insertAtFront = false)
        {
            WColumnReferenceExpression colRef = new WColumnReferenceExpression(tableAlias, columnName);
            colRef.ColumnGraphType = type;

            if (insertAtFront)
            {
                foreach (WColumnReferenceExpression column in this.RawRecordLayout.Keys.ToList())
                {
                    ++this.RawRecordLayout[column];
                    if (this.ParentContextRawRecordLayout != null && this.ParentContextRawRecordLayout.ContainsKey(column))
                    {
                        ++this.ParentContextRawRecordLayout[column];
                    }
                }
                this.RawRecordLayout[colRef] = 0;
            }
            else
            {
                int index = this.RawRecordLayout.Count;
                this.RawRecordLayout[colRef] = index;
            }
        }

        public void ClearField()
        {
            RawRecordLayout.Clear();
        }

        public TemporaryTableHeader ToTableHeader()
        {
            TemporaryTableHeader header = new TemporaryTableHeader();
            foreach (var pair in RawRecordLayout.OrderBy(e => e.Value))
            {
                header.AddColumn(pair.Key.ColumnName, pair.Key.ColumnGraphType, pair.Value);
            }

            return header;
        }

        /// <summary>
        /// Given a column reference, i.e., the composite key of (table alias, column name), 
        /// return its offset in raw records produced by the SQL statement.
        /// </summary>
        /// <param name="tableAlias">Table alias</param>
        /// <param name="columnName">Column name</param>
        /// <returns>The offset of the column reference in the raw records</returns>
        public int LocateColumnReference(string tableAlias, string columnName)
        {
            WColumnReferenceExpression targetName = new WColumnReferenceExpression(tableAlias, columnName);
            if (RawRecordLayout.ContainsKey(targetName))
            {
                return RawRecordLayout[targetName];
            }
            else
            {
                throw new QueryCompilationException(string.Format("Column reference {0}.{1} cannot be located in the raw records in the current execution pipeline.",
                    tableAlias, columnName));
            }
        }

        /// <summary>
        /// Given a column reference, returns its offset in the raw records produced by the SQL statement.
        /// </summary>
        /// <param name="columnReference"></param>
        /// <returns></returns>
        public int LocateColumnReference(WColumnReferenceExpression columnReference)
        {
            if (RawRecordLayout.ContainsKey(columnReference))
            {
                return RawRecordLayout[columnReference];
            }
            else
            {
                throw new QueryCompilationException(string.Format("Column reference {0} cannot be located in the raw records in the current execution pipeline.",
                    columnReference.ToString("")));
            }
        }

        public bool TryLocateColumnReference(WColumnReferenceExpression columnReference, out int columnIndex)
        {
            return RawRecordLayout.TryGetValue(columnReference, out columnIndex);
        }
    }
}

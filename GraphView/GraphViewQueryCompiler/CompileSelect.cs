﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace GraphView
{
    partial class WSelectQueryBlock
    {
        internal override GraphViewExecutionOperator Generate(GraphViewConnection pConnection)
        {
            if (WithPathClause != null) WithPathClause.Generate(pConnection);
            // Construct Match graph for later use
            MatchGraph graph = ConstructGraph();
            // Construct the traversal chain
            ConstructTraversalChain(graph);
            // Construct a header for the operators.
            Dictionary<string, string> columnToAliasDict;
            Dictionary<string, DColumnReferenceExpression> headerToColumnRefDict;
            List<string> header = ConstructHeader(graph, out columnToAliasDict, out headerToColumnRefDict);
            // Attach pre-generated docDB script to the node on Match graph, 
            // and turn predicates that cannot be attached to one node into boolean function.
            List<BooleanFunction> Functions = AttachScriptSegment(graph, header, columnToAliasDict, headerToColumnRefDict);
            // Construct operators accroding to the match graph, header and boolean function list.
            return ConstructOperator(graph, header, columnToAliasDict, pConnection, Functions);
        }

        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewConnection dbConnection)
        {
            List<WTableReferenceWithAlias> nonVertexTableReferences = null;
            MatchGraph graphPattern = ConstructGraph2(context.TableReferences, out nonVertexTableReferences);

            // Vertex and edge aliases from the graph pattern, plus non-vertex table references.
            List<string> vertexAndEdgeAliases = new List<string>();

            foreach (var subGraph in graphPattern.ConnectedSubGraphs)
            {
                vertexAndEdgeAliases.AddRange(subGraph.Nodes.Keys);
                vertexAndEdgeAliases.AddRange(subGraph.Edges.Keys);
            }
            foreach (var nonVertexTableReference in nonVertexTableReferences)
            {
                vertexAndEdgeAliases.Add(nonVertexTableReference.Alias.Value);
            }

            // Normalizes the search condition into conjunctive predicates
            BooleanExpressionNormalizeVisitor booleanNormalize = new BooleanExpressionNormalizeVisitor();
            List<WBooleanExpression> conjunctivePredicates = 
                WhereClause != null && WhereClause.SearchCondition != null ?
                booleanNormalize.Invoke(WhereClause.SearchCondition) :
                new List<WBooleanExpression>();

            // A list of predicates and their accessed table references 
            // Predicates in this list are those that cannot be assigned to the match graph
            List<Tuple<WBooleanExpression, HashSet<string>>>
                predicatesAccessedTableReferences = new List<Tuple<WBooleanExpression, HashSet<string>>>();
            AccessedTableColumnVisitor columnVisitor = new AccessedTableColumnVisitor();

            foreach (WBooleanExpression predicate in conjunctivePredicates)
            {
                bool isOnlyTargetTableReferenced;
                Dictionary<string, HashSet<string>> tableColumnReferences = columnVisitor.Invoke(predicate,
                    vertexAndEdgeAliases, out isOnlyTargetTableReferenced);

                if (!isOnlyTargetTableReferenced || !TryAttachPredicate(graphPattern, predicate, tableColumnReferences))
                {
                    // Attach cross-table predicate's referencing properties for later runtime evaluation
                    AttachProperties(graphPattern, tableColumnReferences);
                    predicatesAccessedTableReferences.Add(
                        new Tuple<WBooleanExpression, HashSet<string>>(predicate,
                            new HashSet<string>(tableColumnReferences.Keys)));
                }
            }

            foreach (WSelectElement selectElement in SelectElements)
            {
                bool isOnlyTargetTableReferenced;
                Dictionary<string, HashSet<string>> tableColumnReferences = columnVisitor.Invoke(selectElement,
                    vertexAndEdgeAliases, out isOnlyTargetTableReferenced);
                // Attach referencing properties for later runtime evaluation or selection
                AttachProperties(graphPattern, tableColumnReferences);
            }

            foreach (var nonVertexTableReference in nonVertexTableReferences)
            {
                bool isOnlyTargetTableReferenced;
                Dictionary<string, HashSet<string>> tableColumnReferences = columnVisitor.Invoke(
                    nonVertexTableReference, vertexAndEdgeAliases, out isOnlyTargetTableReferenced);
                // Attach referencing properties for later runtime evaluation
                AttachProperties(graphPattern, tableColumnReferences);
            }

            ConstructTraversalChain2(graphPattern);

            ConstructJsonQueries(graphPattern);

            return ConstructOperator2(dbConnection, graphPattern, context, nonVertexTableReferences,
                predicatesAccessedTableReferences);
        }

        /// <summary>
        /// If a predicate is a cross-table one, return false
        /// Otherwise, attach the predicate to the corresponding node or edge and return true
        /// </summary>
        /// <param name="graphPattern"></param>
        /// <param name="predicate"></param>
        /// <param name="tableColumnReferences"></param>
        private bool TryAttachPredicate(MatchGraph graphPattern, WBooleanExpression predicate, Dictionary<string, HashSet<string>> tableColumnReferences)
        {
            // Attach fail if it is a cross-table predicate
            if (tableColumnReferences.Count > 1)
                return false;
            MatchEdge edge;
            MatchNode node;
            bool attachFlag = false;

            foreach (var tableColumnReference in tableColumnReferences)
            {
                var tableName = tableColumnReference.Key;
                var properties = tableColumnReference.Value;

                if (graphPattern.TryGetEdge(tableName, out edge))
                {
                    if (edge.Predicates == null)
                        edge.Predicates = new List<WBooleanExpression>();
                    edge.Predicates.Add(predicate);
                    // Attach edge's propeties for later runtime evaluation
                    AttachProperties(graphPattern, new Dictionary<string, HashSet<string>> {{tableName, properties}});
                    attachFlag = true;
                }
                else if (graphPattern.TryGetNode(tableName, out node))
                {
                    if (node.Predicates == null)
                        node.Predicates = new List<WBooleanExpression>();
                    node.Predicates.Add(predicate);
                    attachFlag = true;
                }
            }

            return attachFlag;
        }

        /// <summary>
        /// Attach referencing properties to corresponding nodes and edges
        /// for later runtime evaluation or selection.
        /// </summary>
        /// <param name="graphPattern"></param>
        /// <param name="tableColumnReferences"></param>
        private void AttachProperties(MatchGraph graphPattern, Dictionary<string, HashSet<string>> tableColumnReferences)
        {
            MatchEdge edge;
            MatchNode node;

            foreach (var tableColumnReference in tableColumnReferences)
            {
                var tableName = tableColumnReference.Key;
                var properties = tableColumnReference.Value;

                if (graphPattern.TryGetEdge(tableName, out edge))
                {
                    if (edge.Properties == null)
                        edge.Properties = new List<string>();
                    foreach (var property in properties)
                    {
                        if (!edge.Properties.Contains(property))
                            edge.Properties.Add(property);
                    }
                }
                else if (graphPattern.TryGetNode(tableName, out node))
                {
                    if (node.Properties == null)
                        node.Properties = new List<string>();
                    foreach (var property in properties)
                    {
                        if (!node.Properties.Contains(property))
                            node.Properties.Add(property);
                    }
                }
            }
        }

        internal static void ConstructJsonQueries(MatchGraph graphPattern)
        {
            foreach (var subGraph in graphPattern.ConnectedSubGraphs)
            {
                var processedNodes = new HashSet<MatchNode>();
                var traversalChain =
                    new Stack<Tuple<MatchNode, MatchEdge, MatchNode, List<MatchEdge>, List<MatchEdge>>>(
                        subGraph.TraversalChain2);
                while (traversalChain.Count != 0)
                {
                    var currentChain = traversalChain.Pop();
                    var sourceNode = currentChain.Item1;
                    var traversalEdge = currentChain.Item2;
                    if (!processedNodes.Contains(sourceNode))
                    {
                        ConstructJsonQueryOnNode(sourceNode);
                        processedNodes.Add(sourceNode);
                    }
                    if (traversalEdge != null)
                    {
                        var sinkNode = traversalEdge.SinkNode;
                        ConstructJsonQueryOnNode(sinkNode, currentChain.Item4);
                        processedNodes.Add(sinkNode);
                    }
                }
            }
        }

        internal static void ConstructJsonQueryOnNode(MatchNode node, List<MatchEdge> backwardMatchingEdges = null)
        {
            var nodeAlias = node.NodeAlias;
            var selectStrBuilder = new StringBuilder();
            var joinStrBuilder = new StringBuilder();
            var properties = new List<string>(node.Properties);
            WBooleanExpression searchCondition = null;

            selectStrBuilder.Append(nodeAlias).Append('.').Append(node.Properties[0]);
            for (var i = 1; i < node.Properties.Count; i++)
            {
                var selectName = nodeAlias;
                if (!"*".Equals(node.Properties[i], StringComparison.OrdinalIgnoreCase))
                    selectName += "." + node.Properties[i];
                selectStrBuilder.Append(", ").Append(selectName);
            }
                

            if (backwardMatchingEdges == null)
                backwardMatchingEdges = new List<MatchEdge>();

            foreach (var predicate in node.Predicates)
                searchCondition = WBooleanBinaryExpression.Conjunction(searchCondition, predicate);

            foreach (var edge in backwardMatchingEdges)
            {
                joinStrBuilder.Append(" Join ")
                    .Append(edge.EdgeAlias)
                    .Append(" in ")
                    .Append(node.NodeAlias)
                    .Append(edge.IsReversed ? "._reverse_edge " : "_edge ");

                foreach (var property in edge.Properties)
                {
                    var selectName = edge.EdgeAlias;
                    var selectAlias = edge.EdgeAlias;
                    if ("*".Equals(property, StringComparison.OrdinalIgnoreCase))
                    {
                        selectAlias += "_" + selectName;
                    }
                    else
                    {
                        selectName += "." + property;
                        selectAlias += "_" + property;
                    }
                        
                    selectStrBuilder.Append(", ").Append(string.Format("{0} AS {1}", selectName, selectAlias));
                    properties.Add(selectAlias);
                }   

                foreach (var predicate in edge.Predicates)
                    searchCondition = WBooleanBinaryExpression.Conjunction(searchCondition, predicate);
            }

            var jsonQuery = new JsonQuery
            {
                Alias = nodeAlias,
                JoinClause = joinStrBuilder.ToString(),
                SelectClause = selectStrBuilder.ToString(),
                WhereSearchCondition = searchCondition != null ? searchCondition.ToString() : null,
                Properties = properties,
                // TODO: ProjectedColumns
                //ProjectedColumns = 
            };
            node.AttachedJsonQuery = jsonQuery;
        }

        private MatchGraph ConstructGraph()
        {
            Dictionary<string, List<string>> EdgeColumnToAliasesDict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, MatchPath> pathDictionary = new Dictionary<string, MatchPath>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, MatchEdge> ReversedEdgeDict = new Dictionary<string, MatchEdge>();

            UnionFind UnionFind = new UnionFind();
            Dictionary<string, MatchNode> Nodes = new Dictionary<string, MatchNode>(StringComparer.OrdinalIgnoreCase);
            List<ConnectedComponent> ConnectedSubGraphs = new List<ConnectedComponent>();
            Dictionary<string, ConnectedComponent> SubGrpahMap = new Dictionary<string, ConnectedComponent>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> Parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            UnionFind.Parent = Parent;

            // Retrive information from the SelectQueryBlcok
            if (FromClause != null)
            {
                foreach (WTableReferenceWithAlias FromReference in FromClause.TableReferences)
                {
                    Nodes.GetOrCreate(FromReference.Alias.Value);
                    if (!Parent.ContainsKey(FromReference.Alias.Value))
                        Parent[FromReference.Alias.Value] = FromReference.Alias.Value;
                }
            }

            // Consturct nodes and edges of a match graph defined by the SelectQueryBlock
            if (MatchClause != null)
            {
                if (MatchClause.Paths.Count > 0)
                {
                    foreach (var path in MatchClause.Paths)
                    {
                        var index = 0;
                        // Consturct the source node of a path in MatchClause.Paths
                        MatchEdge EdgeToSrcNode = null;
                        for (var count = path.PathEdgeList.Count; index < count; ++index)
                        {
                            var CurrentNodeTableRef = path.PathEdgeList[index].Item1;
                            var CurrentEdgeColumnRef = path.PathEdgeList[index].Item2;
                            var CurrentNodeExposedName = CurrentNodeTableRef.BaseIdentifier.Value;
                            var nextNodeTableRef = index != count - 1
                                ? path.PathEdgeList[index + 1].Item1
                                : path.Tail;
                            var nextNodeExposedName = nextNodeTableRef.BaseIdentifier.Value;
                            var SrcNode = Nodes.GetOrCreate(CurrentNodeExposedName);
                            if (SrcNode.NodeAlias == null)
                            {
                                SrcNode.NodeAlias = CurrentNodeExposedName;
                                SrcNode.Neighbors = new List<MatchEdge>();
                                SrcNode.ReverseNeighbors = new List<MatchEdge>();
                                SrcNode.External = false;
                                SrcNode.Predicates = new List<WBooleanExpression>();
                                SrcNode.ReverseCheckList = new Dictionary<int, int>();
                                SrcNode.HeaderLength = 0;
                            }

                            // Consturct the edge of a path in MatchClause.Paths
                            string EdgeAlias = CurrentEdgeColumnRef.Alias;
                            if (EdgeAlias == null)
                            {
                                bool isReversed = path.IsReversed;
                                var CurrentEdgeName = CurrentEdgeColumnRef.MultiPartIdentifier.Identifiers.Last().Value;
                                string originalEdgeName = null;

                                EdgeAlias = string.Format("{0}_{1}_{2}", CurrentNodeExposedName, CurrentEdgeName,
                                    nextNodeExposedName);

                                // when current edge is a reversed edge, the key should still be the original edge name
                                var edgeNameKey = isReversed ? originalEdgeName : CurrentEdgeName;
                                if (EdgeColumnToAliasesDict.ContainsKey(edgeNameKey))
                                {
                                    EdgeColumnToAliasesDict[edgeNameKey].Add(EdgeAlias);
                                }
                                else
                                {
                                    EdgeColumnToAliasesDict.Add(edgeNameKey, new List<string> { EdgeAlias });
                                }
                            }

                            MatchEdge EdgeFromSrcNode;
                            if (CurrentEdgeColumnRef.MinLength == 1 && CurrentEdgeColumnRef.MaxLength == 1)
                            {
                                EdgeFromSrcNode = new MatchEdge
                                {
                                    SourceNode = SrcNode,
                                    EdgeColumn = CurrentEdgeColumnRef,
                                    EdgeAlias = EdgeAlias,
                                    Predicates = new List<WBooleanExpression>(),
                                    BindNodeTableObjName =
                                        new WSchemaObjectName(
                                            ),
                                    IsReversed = false,
                                };
                            }
                            else
                            {
                                MatchPath matchPath = new MatchPath
                                {
                                    SourceNode = SrcNode,
                                    EdgeColumn = CurrentEdgeColumnRef,
                                    EdgeAlias = EdgeAlias,
                                    Predicates = new List<WBooleanExpression>(),
                                    BindNodeTableObjName =
                                        new WSchemaObjectName(
                                            ),
                                    MinLength = CurrentEdgeColumnRef.MinLength,
                                    MaxLength = CurrentEdgeColumnRef.MaxLength,
                                    ReferencePathInfo = false,
                                    AttributeValueDict = CurrentEdgeColumnRef.AttributeValueDict,
                                    IsReversed = false,
                                };
                                pathDictionary[EdgeAlias] = matchPath;
                                EdgeFromSrcNode = matchPath;
                            }

                            if (EdgeToSrcNode != null)
                            {
                                EdgeToSrcNode.SinkNode = SrcNode;
                                if (!(EdgeToSrcNode is MatchPath))
                                {
                                    //Add ReverseEdge
                                    MatchEdge reverseEdge = new MatchEdge
                                    {
                                        SourceNode = EdgeToSrcNode.SinkNode,
                                        SinkNode = EdgeToSrcNode.SourceNode,
                                        EdgeColumn = EdgeToSrcNode.EdgeColumn,
                                        EdgeAlias = EdgeToSrcNode.EdgeAlias,
                                        Predicates = EdgeToSrcNode.Predicates,
                                        BindNodeTableObjName =
                                            new WSchemaObjectName(
                                            ),
                                        IsReversed = true,
                                    };
                                    SrcNode.ReverseNeighbors.Add(reverseEdge);
                                    ReversedEdgeDict[EdgeToSrcNode.EdgeAlias] = reverseEdge;
                                }
                            }

                            EdgeToSrcNode = EdgeFromSrcNode;

                            if (!Parent.ContainsKey(CurrentNodeExposedName))
                                Parent[CurrentNodeExposedName] = CurrentNodeExposedName;
                            if (!Parent.ContainsKey(nextNodeExposedName))
                                Parent[nextNodeExposedName] = nextNodeExposedName;

                            UnionFind.Union(CurrentNodeExposedName, nextNodeExposedName);

                            SrcNode.Neighbors.Add(EdgeFromSrcNode);


                        }
                        // Consturct destination node of a path in MatchClause.Paths
                        var tailExposedName = path.Tail.BaseIdentifier.Value;
                        var DestNode = Nodes.GetOrCreate(tailExposedName);
                        if (DestNode.NodeAlias == null)
                        {
                            DestNode.NodeAlias = tailExposedName;
                            DestNode.Neighbors = new List<MatchEdge>();
                            DestNode.ReverseNeighbors = new List<MatchEdge>();
                            DestNode.Predicates = new List<WBooleanExpression>();
                            DestNode.ReverseCheckList = new Dictionary<int, int>();
                            DestNode.HeaderLength = 0;
                        }
                        if (EdgeToSrcNode != null)
                        {
                            EdgeToSrcNode.SinkNode = DestNode;
                            if (!(EdgeToSrcNode is MatchPath))
                            {
                                //Add ReverseEdge
                                MatchEdge reverseEdge = new MatchEdge
                                {
                                    SourceNode = EdgeToSrcNode.SinkNode,
                                    SinkNode = EdgeToSrcNode.SourceNode,
                                    EdgeColumn = EdgeToSrcNode.EdgeColumn,
                                    EdgeAlias = EdgeToSrcNode.EdgeAlias,
                                    Predicates = EdgeToSrcNode.Predicates,
                                    BindNodeTableObjName =
                                        new WSchemaObjectName(
                                        ),
                                    IsReversed = true,
                                };
                                DestNode.ReverseNeighbors.Add(reverseEdge);
                                ReversedEdgeDict[EdgeToSrcNode.EdgeAlias] = reverseEdge;
                            }
                            
                        }
                    }

                }
            }
            // Use union find algorithmn to define which subgraph does a node belong to and put it into where it belongs to.
            foreach (var node in Nodes)
            {
                string root;

                root = UnionFind.Find(node.Key);  // put them into the same graph

                var patternNode = node.Value;

                if (patternNode.NodeAlias == null)
                {
                    patternNode.NodeAlias = node.Key;
                    patternNode.Neighbors = new List<MatchEdge>();
                    patternNode.ReverseNeighbors = new List<MatchEdge>();
                    patternNode.External = false;
                    patternNode.Predicates = new List<WBooleanExpression>();
                }

                if (!SubGrpahMap.ContainsKey(root))
                {
                    var subGraph = new ConnectedComponent();
                    subGraph.Nodes[node.Key] = node.Value;
                    foreach (var edge in node.Value.Neighbors)
                    {
                        subGraph.Edges[edge.EdgeAlias] = edge;
                    }
                    SubGrpahMap[root] = subGraph;
                    ConnectedSubGraphs.Add(subGraph);
                    subGraph.IsTailNode[node.Value] = false;
                }
                else
                {
                    var subGraph = SubGrpahMap[root];
                    subGraph.Nodes[node.Key] = node.Value;
                    foreach (var edge in node.Value.Neighbors)
                    {
                        subGraph.Edges[edge.EdgeAlias] = edge;
                    }
                    subGraph.IsTailNode[node.Value] = false;
                }
            }

            // Combine all subgraphs into a complete match graph and return it
            MatchGraph Graph = new MatchGraph
            {
                ConnectedSubGraphs = ConnectedSubGraphs,
                ReversedEdgeDict = ReversedEdgeDict,
            };

            return Graph;
        }

        private MatchGraph ConstructGraph2(
            Dictionary<string, TableGraphType> outerContextTableReferences,
            out List<WTableReferenceWithAlias> nonVertexTableReferences)
        {
            nonVertexTableReferences = new List<WTableReferenceWithAlias>();

            Dictionary<string, MatchPath> pathDictionary = new Dictionary<string, MatchPath>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, MatchEdge> reversedEdgeDict = new Dictionary<string, MatchEdge>();

            UnionFind unionFind = new UnionFind();
            Dictionary<string, MatchNode> vertexTableCollection = new Dictionary<string, MatchNode>(StringComparer.OrdinalIgnoreCase);
            List<ConnectedComponent> connectedSubGraphs = new List<ConnectedComponent>();
            Dictionary<string, ConnectedComponent> subGraphMap = new Dictionary<string, ConnectedComponent>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            unionFind.Parent = parent;

            // Goes through the FROM clause and extracts vertex table references and non-vertex table references
            if (FromClause != null)
            {
                List<WNamedTableReference> vertexTableList = new List<WNamedTableReference>();
                TableClassifyVisitor tcVisitor = new TableClassifyVisitor();
                tcVisitor.Invoke(FromClause, vertexTableList, nonVertexTableReferences);

                foreach (WNamedTableReference vertexTableRef in vertexTableList)
                {
                    vertexTableCollection.GetOrCreate(vertexTableRef.Alias.Value);
                    if (!parent.ContainsKey(vertexTableRef.Alias.Value))
                        parent[vertexTableRef.Alias.Value] = vertexTableRef.Alias.Value;
                }
            }

            // Consturct nodes and edges of a match graph defined by the SelectQueryBlock
            if (MatchClause != null)
            {
                if (MatchClause.Paths.Count > 0)
                {
                    foreach (var path in MatchClause.Paths)
                    {
                        var index = 0;
                        // Consturct the source node of a path in MatchClause.Paths
                        MatchEdge EdgeToSrcNode = null;
                        for (var count = path.PathEdgeList.Count; index < count; ++index)
                        {
                            var CurrentNodeTableRef = path.PathEdgeList[index].Item1;
                            var CurrentEdgeColumnRef = path.PathEdgeList[index].Item2;
                            var CurrentNodeExposedName = CurrentNodeTableRef.BaseIdentifier.Value;
                            var nextNodeTableRef = index != count - 1
                                ? path.PathEdgeList[index + 1].Item1
                                : path.Tail;
                            MatchNode SrcNode = vertexTableCollection.GetOrCreate(CurrentNodeExposedName);

                            // Check whether the vertex is defined in outer context
                            if (!vertexTableCollection.TryGetValue(CurrentNodeExposedName, out SrcNode))
                            {
                                if (!outerContextTableReferences.ContainsKey(CurrentNodeExposedName))
                                    throw new GraphViewException("Table " + CurrentNodeExposedName + " doesn't exist in the context.");
                                SrcNode = new MatchNode { IsFromOuterContext = true };
                                vertexTableCollection.Add(CurrentNodeExposedName, SrcNode);
                            }
                            if (SrcNode.NodeAlias == null)
                            {
                                SrcNode.NodeAlias = CurrentNodeExposedName;
                                SrcNode.Neighbors = new List<MatchEdge>();
                                SrcNode.ReverseNeighbors = new List<MatchEdge>();
                                SrcNode.DanglingEdges = new List<MatchEdge>();
                                SrcNode.External = false;
                                SrcNode.Predicates = new List<WBooleanExpression>();
                                SrcNode.ReverseCheckList = new Dictionary<int, int>();
                                SrcNode.HeaderLength = 0;
                                SrcNode.Properties = new List<string> {"id", "_edge", "_reverse_edge"};
                            }

                            // Consturct the edge of a path in MatchClause.Paths
                            string EdgeAlias = CurrentEdgeColumnRef.Alias;
                            //if (EdgeAlias == null)
                            //{
                            //    var CurrentEdgeName = CurrentEdgeColumnRef.MultiPartIdentifier.Identifiers.Last().Value;
                            //    EdgeAlias = string.Format("{0}_{1}_{2}", CurrentNodeExposedName, CurrentEdgeName,
                            //        nextNodeExposedName);
                            //}

                            MatchEdge EdgeFromSrcNode;
                            if (CurrentEdgeColumnRef.MinLength == 1 && CurrentEdgeColumnRef.MaxLength == 1)
                            {
                                EdgeFromSrcNode = new MatchEdge
                                {
                                    SourceNode = SrcNode,
                                    EdgeColumn = CurrentEdgeColumnRef,
                                    EdgeAlias = EdgeAlias,
                                    Predicates = new List<WBooleanExpression>(),
                                    BindNodeTableObjName =
                                        new WSchemaObjectName(
                                            ),
                                    IsReversed = false,
                                    EdgeType = CurrentEdgeColumnRef.EdgeType,
                                    Properties = new List<string> { "_sink", "_ID" },
                                };
                            }
                            else
                            {
                                MatchPath matchPath = new MatchPath
                                {
                                    SourceNode = SrcNode,
                                    EdgeColumn = CurrentEdgeColumnRef,
                                    EdgeAlias = EdgeAlias,
                                    Predicates = new List<WBooleanExpression>(),
                                    BindNodeTableObjName =
                                        new WSchemaObjectName(
                                            ),
                                    MinLength = CurrentEdgeColumnRef.MinLength,
                                    MaxLength = CurrentEdgeColumnRef.MaxLength,
                                    ReferencePathInfo = false,
                                    AttributeValueDict = CurrentEdgeColumnRef.AttributeValueDict,
                                    IsReversed = false,
                                    EdgeType = CurrentEdgeColumnRef.EdgeType,
                                    Properties = new List<string> { "_sink", "_ID" },
                                };
                                pathDictionary[EdgeAlias] = matchPath;
                                EdgeFromSrcNode = matchPath;
                            }
                            // Check whether the edge is defined in the outer context
                            //TableGraphType tableGraphType;
                            //if (outerContextTableReferences.TryGetValue(EdgeAlias, out tableGraphType && 
                            //    tableGraphType == TableGraphType.Edge)
                            if (outerContextTableReferences.ContainsKey(EdgeAlias))
                                EdgeFromSrcNode.IsFromOuterContext = true;

                            if (EdgeToSrcNode != null)
                            {
                                EdgeToSrcNode.SinkNode = SrcNode;
                                if (!(EdgeToSrcNode is MatchPath))
                                {
                                    //Add ReverseEdge
                                    MatchEdge reverseEdge = new MatchEdge
                                    {
                                        SourceNode = EdgeToSrcNode.SinkNode,
                                        SinkNode = EdgeToSrcNode.SourceNode,
                                        EdgeColumn = EdgeToSrcNode.EdgeColumn,
                                        EdgeAlias = EdgeToSrcNode.EdgeAlias,
                                        Predicates = EdgeToSrcNode.Predicates,
                                        BindNodeTableObjName =
                                            new WSchemaObjectName(
                                            ),
                                        IsReversed = true,
                                        EdgeType = EdgeToSrcNode.EdgeType,
                                        Properties = new List<string> { "_sink", "_ID" },
                                    };
                                    SrcNode.ReverseNeighbors.Add(reverseEdge);
                                    reversedEdgeDict[EdgeToSrcNode.EdgeAlias] = reverseEdge;
                                }
                            }

                            EdgeToSrcNode = EdgeFromSrcNode;

                            if (!parent.ContainsKey(CurrentNodeExposedName))
                                parent[CurrentNodeExposedName] = CurrentNodeExposedName;

                            var nextNodeExposedName = nextNodeTableRef != null ? nextNodeTableRef.BaseIdentifier.Value : null;
                            if (nextNodeExposedName != null)
                            {
                                if (!parent.ContainsKey(nextNodeExposedName))
                                    parent[nextNodeExposedName] = nextNodeExposedName;

                                unionFind.Union(CurrentNodeExposedName, nextNodeExposedName);

                                SrcNode.Neighbors.Add(EdgeFromSrcNode);

                            }
                            // Dangling edge without SinkNode
                            else
                            {
                                SrcNode.DanglingEdges.Add(EdgeFromSrcNode);
                            }
                        }
                        if (path.Tail == null) continue;
                        // Consturct destination node of a path in MatchClause.Paths
                        var tailExposedName = path.Tail.BaseIdentifier.Value;
                        MatchNode DestNode;
                        // Check whether the vertex is defined in outer context
                        if (!vertexTableCollection.TryGetValue(tailExposedName, out DestNode))
                        {
                            if (!outerContextTableReferences.ContainsKey(tailExposedName))
                                throw new GraphViewException("Table " + tailExposedName + " doesn't exist in the context.");
                            DestNode = new MatchNode { IsFromOuterContext = true };
                            vertexTableCollection.Add(tailExposedName, DestNode);
                        }
                        if (DestNode.NodeAlias == null)
                        {
                            DestNode.NodeAlias = tailExposedName;
                            DestNode.Neighbors = new List<MatchEdge>();
                            DestNode.ReverseNeighbors = new List<MatchEdge>();
                            DestNode.DanglingEdges = new List<MatchEdge>();
                            DestNode.Predicates = new List<WBooleanExpression>();
                            DestNode.ReverseCheckList = new Dictionary<int, int>();
                            DestNode.HeaderLength = 0;
                            DestNode.Properties = new List<string> { "id", "_edge", "_reverse_edge" };
                        }
                        if (EdgeToSrcNode != null)
                        {
                            EdgeToSrcNode.SinkNode = DestNode;
                            if (!(EdgeToSrcNode is MatchPath))
                            {
                                //Add ReverseEdge
                                MatchEdge reverseEdge = new MatchEdge
                                {
                                    SourceNode = EdgeToSrcNode.SinkNode,
                                    SinkNode = EdgeToSrcNode.SourceNode,
                                    EdgeColumn = EdgeToSrcNode.EdgeColumn,
                                    EdgeAlias = EdgeToSrcNode.EdgeAlias,
                                    Predicates = EdgeToSrcNode.Predicates,
                                    BindNodeTableObjName =
                                        new WSchemaObjectName(
                                        ),
                                    IsReversed = true,
                                    EdgeType = EdgeToSrcNode.EdgeType,
                                    Properties = new List<string> { "_sink", "_ID" },
                                };
                                DestNode.ReverseNeighbors.Add(reverseEdge);
                                reversedEdgeDict[EdgeToSrcNode.EdgeAlias] = reverseEdge;
                            }

                        }
                    }

                }
            }
            // Use union find algorithmn to define which subgraph does a node belong to and put it into where it belongs to.
            foreach (var node in vertexTableCollection)
            {
                string root;

                root = unionFind.Find(node.Key);  // put them into the same graph

                var patternNode = node.Value;

                if (patternNode.NodeAlias == null)
                {
                    patternNode.NodeAlias = node.Key;
                    patternNode.Neighbors = new List<MatchEdge>();
                    patternNode.ReverseNeighbors = new List<MatchEdge>();
                    patternNode.DanglingEdges = new List<MatchEdge>();
                    patternNode.External = false;
                    patternNode.Predicates = new List<WBooleanExpression>();
                    patternNode.Properties = new List<string> { "id", "_edge", "_reverse_edge" };
                }

                if (!subGraphMap.ContainsKey(root))
                {
                    var subGraph = new ConnectedComponent();
                    subGraph.Nodes[node.Key] = node.Value;
                    foreach (var edge in node.Value.Neighbors)
                    {
                        subGraph.Edges[edge.EdgeAlias] = edge;
                    }
                    foreach (var edge in node.Value.DanglingEdges)
                    {
                        subGraph.Edges[edge.EdgeAlias] = edge;
                    }
                    subGraphMap[root] = subGraph;
                    connectedSubGraphs.Add(subGraph);
                    subGraph.IsTailNode[node.Value] = false;
                }
                else
                {
                    var subGraph = subGraphMap[root];
                    subGraph.Nodes[node.Key] = node.Value;
                    foreach (var edge in node.Value.Neighbors)
                    {
                        subGraph.Edges[edge.EdgeAlias] = edge;
                    }
                    foreach (var edge in node.Value.DanglingEdges)
                    {
                        subGraph.Edges[edge.EdgeAlias] = edge;
                    }
                    subGraph.IsTailNode[node.Value] = false;
                }
            }

            // Combine all subgraphs into a complete match graph and return it
            MatchGraph graphPattern = new MatchGraph
            {
                ConnectedSubGraphs = connectedSubGraphs,
                ReversedEdgeDict = reversedEdgeDict,
            };

            return graphPattern;
        }

        private List<BooleanFunction> AttachScriptSegment(MatchGraph graph, List<string> header, Dictionary<string, string> columnToAliasDict, 
            Dictionary<string, DColumnReferenceExpression> headerToColumnRefDict)
        {
            // Call attach predicate visitor to attach predicates on nodes.
            AttachWhereClauseVisitor AttachPredicateVistor = new AttachWhereClauseVisitor();
            QueryCompilationContext Context = new QueryCompilationContext();
            // GraphMetaData GraphMeta = new GraphMetaData();
            // Dictionary<string, string> ColumnTableMapping = Context.GetColumnToAliasMapping(GraphMeta.ColumnsOfNodeTables);
            AttachPredicateVistor.Invoke(WhereClause, graph);
            List<BooleanFunction> BooleanList = new List<BooleanFunction>();

            // If some predictaes are failed to be assigned to one node, turn them into boolean functions
            foreach (var predicate in AttachPredicateVistor.FailedToAssign)
            {
                // Analyse what kind of predicates they are, and generate corresponding boolean functions.
                if (predicate is WBooleanComparisonExpression)
                {
                    var FirstColumnExpr = ((predicate as WBooleanComparisonExpression).FirstExpr) as WColumnReferenceExpression;
                    var SecondColumnExpr = ((predicate as WBooleanComparisonExpression).SecondExpr) as WColumnReferenceExpression;
                    if (FirstColumnExpr == null || SecondColumnExpr == null)
                        throw new GraphViewException("Cross documents predicate: " + predicate.ToString() + " not supported yet.");
                    string FirstExpr = FirstColumnExpr.ToString();
                    string SecondExpr = SecondColumnExpr.ToString();

                    var insertIdx = header.Count > 0 ? header.Count-1 : 0;
                    if (header.IndexOf(FirstExpr) == -1)
                    {
                        header.Insert(insertIdx++, FirstExpr);
                        columnToAliasDict.Add(FirstExpr, FirstExpr);
                        headerToColumnRefDict[FirstExpr] = new DColumnReferenceExpression
                        {
                            ColumnName = FirstExpr,
                            MultiPartIdentifier = new DMultiPartIdentifier(FirstColumnExpr.MultiPartIdentifier)
                        };
                    }
                    if (header.IndexOf(SecondExpr) == -1)
                    {
                        header.Insert(insertIdx, SecondExpr);
                        columnToAliasDict.Add(SecondExpr, SecondExpr);
                        headerToColumnRefDict[SecondExpr] = new DColumnReferenceExpression
                        {
                            ColumnName = SecondExpr,
                            MultiPartIdentifier = new DMultiPartIdentifier(SecondColumnExpr.MultiPartIdentifier)
                        };
                    }
                    var lhs = columnToAliasDict[FirstExpr];
                    var rhs = columnToAliasDict[SecondExpr];
                    FieldComparisonFunction NewCBF = null;
                    if ((predicate as WBooleanComparisonExpression).ComparisonType == BooleanComparisonType.Equals)
                        NewCBF = new FieldComparisonFunction(lhs, rhs,
                            ComparisonBooleanFunction.ComparisonType.eq);
                    if ((predicate as WBooleanComparisonExpression).ComparisonType ==
                        BooleanComparisonType.NotEqualToExclamation)
                        NewCBF = new FieldComparisonFunction(lhs, rhs,
                            ComparisonBooleanFunction.ComparisonType.neq);
                    if ((predicate as WBooleanComparisonExpression).ComparisonType ==
    BooleanComparisonType.LessThan)
                        NewCBF = new FieldComparisonFunction(lhs, rhs,
                            ComparisonBooleanFunction.ComparisonType.lt);
                    if ((predicate as WBooleanComparisonExpression).ComparisonType ==
    BooleanComparisonType.GreaterThan)
                        NewCBF = new FieldComparisonFunction(lhs, rhs,
                            ComparisonBooleanFunction.ComparisonType.gt);
                    if ((predicate as WBooleanComparisonExpression).ComparisonType ==
    BooleanComparisonType.GreaterThanOrEqualTo)
                        NewCBF = new FieldComparisonFunction(lhs, rhs,
                            ComparisonBooleanFunction.ComparisonType.gte);
                    if ((predicate as WBooleanComparisonExpression).ComparisonType ==
    BooleanComparisonType.LessThanOrEqualTo)
                        NewCBF = new FieldComparisonFunction(lhs, rhs,
                            ComparisonBooleanFunction.ComparisonType.lte);
                    BooleanList.Add(NewCBF);
                }
            }
            // Calculate the start index of select elements
            int StartOfResult =
                graph.ConnectedSubGraphs.Sum(
                    subgraph => subgraph.Nodes.Select(n => n.Value.HeaderLength).Aggregate(0, (cur, next) => cur + next));
            foreach (var subgraph in graph.ConnectedSubGraphs)
            {
                var SortedNodeList = new Stack<Tuple<MatchNode, MatchEdge>>(subgraph.TraversalChain);
                var NodeToMatEdgesDict = subgraph.NodeToMaterializedEdgesDict;
                // Marking which node has been processed for later reverse checking.  
                List<string> ProcessedNodeList = new List<string>();
                // Build query segment on both source node and dest node, 
                while (SortedNodeList.Count != 0)
                {
                    MatchNode CurrentProcessingNode = null;
                    var TargetNode = SortedNodeList.Pop();
                    if (!ProcessedNodeList.Contains(TargetNode.Item1.NodeAlias))
                    {
                        CurrentProcessingNode = TargetNode.Item1;
                        BuildQuerySegementOnNode(ProcessedNodeList, CurrentProcessingNode, header, NodeToMatEdgesDict, columnToAliasDict, headerToColumnRefDict, StartOfResult);
                        ProcessedNodeList.Add(CurrentProcessingNode.NodeAlias);
                    }
                    if (TargetNode.Item2 != null)
                    {
                        CurrentProcessingNode = TargetNode.Item2.SinkNode;
                        BuildQuerySegementOnNode(ProcessedNodeList, CurrentProcessingNode, header, NodeToMatEdgesDict, columnToAliasDict, headerToColumnRefDict, StartOfResult, TargetNode.Item2 is MatchPath);
                        ProcessedNodeList.Add(CurrentProcessingNode.NodeAlias);
                    }
                }
            }
            return BooleanList;
        }

        private void ConstructTraversalChain(MatchGraph graph)
        {
            var graphOptimizer = new DocDbGraphOptimizer(graph);
            foreach (var subGraph in graph.ConnectedSubGraphs)
            {
                // <node, node's edges which need to be pulled from the server>
                Dictionary<string, List<Tuple<MatchEdge, MaterializedEdgeType>>> nodeToMatEdgesDict;
                subGraph.TraversalChain = graphOptimizer.GetOptimizedTraversalOrder(subGraph, out nodeToMatEdgesDict);
                subGraph.NodeToMaterializedEdgesDict = nodeToMatEdgesDict;
            }
        }

        private void ConstructTraversalChain2(MatchGraph graphPattern)
        {
            var graphOptimizer = new DocDbGraphOptimizer(graphPattern);
            foreach (var subGraph in graphPattern.ConnectedSubGraphs)
            {
                subGraph.TraversalChain2 = graphOptimizer.GetOptimizedTraversalOrder2(subGraph);
            }
        }

        private List<int> LocateAdjacencyListIndexes(QueryCompilationContext context, MatchEdge edge)
        {
            var edgeIndex =
                context.LocateColumnReference(new WColumnReferenceExpression(edge.SourceNode.NodeAlias, "_edge"));
            var reverseEdgeIndex =
                context.LocateColumnReference(new WColumnReferenceExpression(edge.SourceNode.NodeAlias, "_reverse_edge"));
            if (edge.EdgeType == WEdgeType.BothEdge)
                return new List<int> { edgeIndex, reverseEdgeIndex };
            else if (edge.IsReversed)
                return new List<int> { reverseEdgeIndex };
            else
                return new List<int> { edgeIndex };
        } 

        internal QueryCompilationContext GenerateLocalContextForAdjacentListDecoder(string edgeTableAlias, List<string> projectedFields)
        {
            var localContext = new QueryCompilationContext();

            var localIndex = 0;
            foreach (var projectedField in projectedFields)
            {
                var columnReference = new WColumnReferenceExpression(edgeTableAlias, projectedField);
                localContext.RawRecordLayout.Add(columnReference, localIndex++);
            }

            return localContext;
        }

        private List<string> ConstructHeader(MatchGraph graph, out Dictionary<string, string> columnToAliasDict, out Dictionary<string, DColumnReferenceExpression> headerToColumnRefDict)
        {
            List<string> header = new List<string>();
            HashSet<string> aliasSet = new HashSet<string>();
            columnToAliasDict = new Dictionary<string, string>();
            headerToColumnRefDict = new Dictionary<string, DColumnReferenceExpression>();
            // Construct the first part of the head which is defined as 
            // |    Node's Alias     {[|  Node's Adjacent list |       _SINK      ][|...n]}
            // |  "node.NodeAlias"   {[|   "edgeAlias_ADJ"     | "edgeAlias_SINK" ][|...n]}
            foreach (var subgraph in graph.ConnectedSubGraphs)
            {
                HashSet<MatchNode> ProcessedNode = new HashSet<MatchNode>();

                var SortedNodes = new Stack<Tuple<MatchNode, MatchEdge>>(subgraph.TraversalChain);
                var nodeToMatEdgesDict = subgraph.NodeToMaterializedEdgesDict;
                while (SortedNodes.Count != 0) {
                    var processingNodePair = SortedNodes.Pop();
                    var srcNode = processingNodePair.Item1;
                    var sinkNode = processingNodePair.Item2?.SinkNode;

                    if (!ProcessedNode.Contains(srcNode))
                    {
                        MatchNode node = srcNode;
                        header.Add(node.NodeAlias);

                        if (nodeToMatEdgesDict != null)
                        {
                            foreach (var t in nodeToMatEdgesDict[srcNode.NodeAlias])
                            {
                                var edge = t.Item1;
                                header.Add(edge.EdgeAlias + "_ADJ");
                                header.Add(edge.EdgeAlias + "_SINK");
                            }
                        }
                        // The meta header length of the node, consisting of node's id and node's outgoing edges
                        // Every edge will have a field as adjList and a field as single sink id
                        // | node id | edge1 | edge1.sink | edge2 | edge2.sink | ...
                        srcNode.HeaderLength = nodeToMatEdgesDict?[srcNode.NodeAlias].Count * 2 + 1 ?? 1;
                        ProcessedNode.Add(node);
                        aliasSet.Add(node.NodeAlias);
                    }
                    if (sinkNode != null && !ProcessedNode.Contains(sinkNode))
                    {
                        MatchNode node = sinkNode;
                        header.Add(node.NodeAlias);

                        if (nodeToMatEdgesDict != null)
                        {
                            foreach (var t in nodeToMatEdgesDict[sinkNode.NodeAlias])
                            {
                                var edge = t.Item1;
                                header.Add(edge.EdgeAlias + "_ADJ");
                                header.Add(edge.EdgeAlias + "_SINK");
                            }
                        }

                        sinkNode.HeaderLength = nodeToMatEdgesDict?[sinkNode.NodeAlias].Count * 2 + 1 ?? 1;
                        ProcessedNode.Add(node);
                        aliasSet.Add(node.NodeAlias);
                    }
                }

                foreach (var edge in subgraph.Edges)
                    aliasSet.Add(edge.Key);
            }
            // Construct the second part of the head which is defined as 
            // ...|Select element|Select element|Select element|...
            // ...|  "ELEMENT1"  |  "ELEMENT2"  |  "ELEMENT3"  |...
            for (var i = 0; i < SelectElements.Count; i++)
            {
                var element = SelectElements[i];
                if (element is WSelectStarExpression)
                {
                    if (FromClause.TableReferences != null && FromClause.TableReferences.Count > 1)
                        throw new GraphViewException("'SELECT *' is only valid with a single input set.");
                    var tr = FromClause.TableReferences[0] as WNamedTableReference;
                    var expr = tr.Alias.Value;
                    var alias = expr + ".doc";
                    header.Add(expr);
                    columnToAliasDict.Add(expr, alias);
                    headerToColumnRefDict[expr] = new DColumnReferenceExpression
                    {
                        ColumnName = alias,
                        MultiPartIdentifier = new DMultiPartIdentifier(expr),
                    };
                    var iden = new Identifier {Value = expr};
                    SelectElements[i] = new WSelectScalarExpression
                    {
                        ColumnName = alias,
                        SelectExpr = new WColumnReferenceExpression { MultiPartIdentifier = new WMultiPartIdentifier(iden) }
                    };
                }
                else if (element is WSelectScalarExpression)
                {
                    var scalarExpr = element as WSelectScalarExpression;
                    if (scalarExpr.SelectExpr is WValueExpression) continue;

                    var column = scalarExpr.SelectExpr as WColumnReferenceExpression;

                    var expr = column.MultiPartIdentifier.ToString();
                    var alias = scalarExpr.ColumnName ?? expr;
                    header.Add(expr);
                    // Add the mapping between the expr and its alias
                    columnToAliasDict.Add(expr, alias);
                    // Add the mapping between the expr and its DColumnReferenceExpr for later normalization
                    headerToColumnRefDict[expr] = new DColumnReferenceExpression
                    {
                        ColumnName = alias,
                        MultiPartIdentifier = new DMultiPartIdentifier(column.MultiPartIdentifier)
                    };
                }
            }

            if (OrderByClause != null && OrderByClause.OrderByElements != null)
            {
                foreach (var element in OrderByClause.OrderByElements)
                {
                    var expr = element.ScalarExpr.ToString();
                    // If true, the expr might need to be added to the header or could not be resolved
                    if (!columnToAliasDict.ContainsKey(expr) && !columnToAliasDict.ContainsValue(expr))
                    {
                        int cutPoint = expr.Length;
                        if (expr.IndexOf('.') != -1) cutPoint = expr.IndexOf('.');
                        var bindObject = expr.Substring(0, cutPoint);
                        if (aliasSet.Contains(bindObject))
                        {
                            header.Add(expr);
                            columnToAliasDict.Add(expr, expr);
                            headerToColumnRefDict[expr] = new DColumnReferenceExpression
                            {
                                ColumnName = expr,
                                MultiPartIdentifier = new DMultiPartIdentifier((element.ScalarExpr as WColumnReferenceExpression).MultiPartIdentifier)
                            };
                        }
                        else
                            throw new GraphViewException(string.Format("The identifier \"{0}\" could not be bound", expr));
                    }
                }
            }
            // Construct a slot for path 
            // ...|   PATH  |...
            // ...|xxx-->yyy|...
            header.Add("PATH");
            return header;
        }

        /// <summary>
        /// Check whether all the tabls referenced by the cross-table predicate have been processed
        /// If so, embed the predicate in a filter operator and append it to the operator list
        /// </summary>
        /// <param name="context"></param>
        /// <param name="connection"></param>
        /// <param name="tableReferences"></param>
        /// <param name="remainingPredicatesAndTheirTableReferences"></param>
        /// <param name="childrenProcessor"></param>
        private void CheckRemainingPredicatesAndAppendFilterOp(QueryCompilationContext context, GraphViewConnection connection,
            HashSet<string> tableReferences,
            List<Tuple<WBooleanExpression, HashSet<string>>> remainingPredicatesAndTheirTableReferences,
            List<GraphViewExecutionOperator> childrenProcessor)
        {
            for (var i = remainingPredicatesAndTheirTableReferences.Count - 1; i >= 0; i--)
            {
                var predicate = remainingPredicatesAndTheirTableReferences[i].Item1;
                var tableRefs = remainingPredicatesAndTheirTableReferences[i].Item2;

                if (tableReferences.IsSupersetOf(tableRefs))
                {
                    childrenProcessor.Add(new FilterOperator(childrenProcessor.Last(),
                        predicate.CompileToFunction(context, connection)));
                    remainingPredicatesAndTheirTableReferences.RemoveAt(i);
                }
            }
        }

        private void CrossApplyEdges(GraphViewConnection connection, QueryCompilationContext context, 
            List<GraphViewExecutionOperator> operatorChain, IList<MatchEdge> edges, 
            List<Tuple<WBooleanExpression, HashSet<string>>> predicatesAccessedTableReferences,
            bool isForwardingEdges = false)
        {
            var tableReferences = context.TableReferences;
            var rawRecordLayout = context.RawRecordLayout;
            foreach (var edge in edges)
            {
                var edgeIndex = LocateAdjacencyListIndexes(context, edge);
                var localEdgeContext = GenerateLocalContextForAdjacentListDecoder(edge.EdgeAlias, edge.Properties);
                var edgePredicates = edge.RetrievePredicatesExpression();
                operatorChain.Add(new AdjacencyListDecoder(
                    operatorChain.Last(),
                    edgeIndex,
                    edgePredicates != null ? edgePredicates.CompileToFunction(localEdgeContext, connection) : null,
                    edge.Properties));

                // Update edge's context info
                tableReferences.Add(edge.EdgeAlias, TableGraphType.Edge);
                UpdateRawRecordLayout(edge.EdgeAlias, edge.Properties, rawRecordLayout);

                if (isForwardingEdges)
                {
                    var sinkNodeIdColumnReference = new WColumnReferenceExpression(edge.SinkNode.NodeAlias, "id");
                    // Add "forwardEdge.sink = sinkNode.id" filter
                    var edgeSinkColumnReference = new WColumnReferenceExpression(edge.EdgeAlias, "_sink");
                    var edgeJoinPredicate = new WBooleanComparisonExpression
                    {
                        ComparisonType = BooleanComparisonType.Equals,
                        FirstExpr = edgeSinkColumnReference,
                        SecondExpr = sinkNodeIdColumnReference
                    };
                    operatorChain.Add(new FilterOperator(operatorChain.Last(),
                        edgeJoinPredicate.CompileToFunction(context, connection)));
                }

                CheckRemainingPredicatesAndAppendFilterOp(context, connection,
                    new HashSet<string>(tableReferences.Keys),
                    predicatesAccessedTableReferences,
                    operatorChain);
            }
        }

        /// <summary>
        /// Update the raw record layout when new properties are added
        /// </summary>
        /// <param name="tableName"></param>
        /// <param name="properties"></param>
        /// <param name="rawRecordLayout"></param>
        private void UpdateRawRecordLayout(string tableName, List<string> properties,
            Dictionary<WColumnReferenceExpression, int> rawRecordLayout)
        {
            var nextLayoutIndex = rawRecordLayout.Count;
            foreach (var property in properties)
            {
                var columnReference = new WColumnReferenceExpression(tableName, property);
                if (!rawRecordLayout.ContainsKey(columnReference))
                    rawRecordLayout.Add(columnReference, nextLayoutIndex++);
            }
        }

        private GraphViewExecutionOperator ConstructOperator2(GraphViewConnection connection, MatchGraph graphPattern,
            QueryCompilationContext context, List<WTableReferenceWithAlias> nonVertexTableReferences,
            List<Tuple<WBooleanExpression, HashSet<string>>> predicatesAccessedTableReferences)
        {
            var operatorChain = new List<GraphViewExecutionOperator>();
            var tableReferences = context.TableReferences;
            var rawRecordLayout = context.RawRecordLayout;

            if (context.OuterContextOp != null)
                context.CurrentExecutionOperator = context.OuterContextOp;

            foreach (var subGraph in graphPattern.ConnectedSubGraphs)
            {
                // For List<MatchEdge>, backwardMatchingEdges in item4 will be cross applied when GetVertices
                // and forwardMatchingEdges in item5 will be cross applied after the TraversalOp
                var traversalChain =
                    new Stack<Tuple<MatchNode, MatchEdge, MatchNode, List<MatchEdge>, List<MatchEdge>>>(
                        subGraph.TraversalChain2);
                var processedNodes = new HashSet<MatchNode>();
                while (traversalChain.Count != 0)
                {
                    var currentChain = traversalChain.Pop();
                    var sourceNode = currentChain.Item1;
                    var traversalEdge = currentChain.Item2;
                    var sinkNode = currentChain.Item3;
                    var backwardMatchingEdges = currentChain.Item4;
                    var forwardMatchingEdges = currentChain.Item5;

                    // The first node in a component
                    if (!processedNodes.Contains(sourceNode))
                    {
                        var fetchNodeOp = new FetchNodeOperator2(connection, sourceNode.AttachedJsonQuery);

                        // The graph contains more than one component
                        if (operatorChain.Any())
                            operatorChain.Add(new CartesianProductOperator2(operatorChain.Last(), fetchNodeOp));
                        else if (context.OuterContextOp != null)
                            operatorChain.Add(new CartesianProductOperator2(context.OuterContextOp, fetchNodeOp));
                        else
                            operatorChain.Add(fetchNodeOp);

                        context.CurrentExecutionOperator = operatorChain.Last();
                        UpdateRawRecordLayout(sourceNode.NodeAlias, sourceNode.Properties, rawRecordLayout);
                        processedNodes.Add(sourceNode);
                        tableReferences.Add(sourceNode.NodeAlias, TableGraphType.Vertex);

                        CheckRemainingPredicatesAndAppendFilterOp(context, connection,
                            new HashSet<string>(tableReferences.Keys), predicatesAccessedTableReferences,
                            operatorChain);

                        // Cross apply dangling Edges
                        CrossApplyEdges(connection, context, operatorChain, sourceNode.DanglingEdges,
                            predicatesAccessedTableReferences);
                    }

                    if (sinkNode != null)
                    {
                        if (WithPathClause2 != null)
                        {
                            
                        }
                        else
                        {
                            // Cross apply the traversal edge and update context info
                            CrossApplyEdges(connection, context, operatorChain, new List<MatchEdge> {traversalEdge},
                                predicatesAccessedTableReferences);

                            var currentEdgeSinkIndex = rawRecordLayout.Count - traversalEdge.Properties.Count;
                            // Generate matching indexes for backwardMatchingEdges
                            var matchingIndexes = new List<Tuple<int, int>>();
                            var localSinkAdjListSinkIndex = sinkNode.Properties.Count;
                            foreach (var backwardMatchingEdge in backwardMatchingEdges)
                            {
                                // backwardEdges.SinkNode.id = backwardEdges.sink
                                var sourceMatchIndex =
                                    rawRecordLayout[new WColumnReferenceExpression(backwardMatchingEdge.SinkNode.NodeAlias, "id")];
                                matchingIndexes.Add(new Tuple<int, int>(sourceMatchIndex, localSinkAdjListSinkIndex));

                                localSinkAdjListSinkIndex += backwardMatchingEdge.Properties.Count;
                            }

                            operatorChain.Add(new TraversalOperator2(operatorChain.Last(), connection,
                                currentEdgeSinkIndex, sinkNode.AttachedJsonQuery, matchingIndexes));

                            // Update sinkNode's context info
                            processedNodes.Add(sinkNode);
                            UpdateRawRecordLayout(sinkNode.NodeAlias, sinkNode.Properties, rawRecordLayout);
                            tableReferences.Add(sinkNode.NodeAlias, TableGraphType.Vertex);

                            // Update backwardEdges' context info
                            foreach (var backwardMatchingEdge in backwardMatchingEdges)
                            {
                                tableReferences.Add(backwardMatchingEdge.EdgeAlias, TableGraphType.Edge);
                                UpdateRawRecordLayout(backwardMatchingEdge.EdgeAlias, backwardMatchingEdge.Properties, rawRecordLayout);
                            }

                            CheckRemainingPredicatesAndAppendFilterOp(context, connection,
                                new HashSet<string>(tableReferences.Keys), predicatesAccessedTableReferences,
                                operatorChain);

                            // Cross apply forwardMatchingEdges
                            CrossApplyEdges(connection, context, operatorChain, forwardMatchingEdges,
                                predicatesAccessedTableReferences, true);

                            // Cross apply dangling edges
                            CrossApplyEdges(connection, context, operatorChain, sinkNode.DanglingEdges,
                                predicatesAccessedTableReferences);
                        }
                    }
                    context.CurrentExecutionOperator = operatorChain.Last();
                }
            }

            foreach (var tableReference in nonVertexTableReferences)
            {
                if (tableReference is WQueryDerivedTable)
                {
                    var derivedQueryExpr = (tableReference as WQueryDerivedTable).QueryExpr;
                    var derivedQueryContext = new QueryCompilationContext(context.TemporaryTableCollection);
                    var derivedQueryOp = derivedQueryExpr.Compile(derivedQueryContext, connection);

                    operatorChain.Add(operatorChain.Any()
                        ? new CartesianProductOperator2(operatorChain.Last(), derivedQueryOp)
                        : derivedQueryOp);

                    foreach (var pair in derivedQueryContext.RawRecordLayout.OrderBy(e => e.Value))
                    {
                        var tableAlias = tableReference.Alias.Value;
                        var columnName = pair.Key.ColumnName;
                        // TODO: Change to correct ColumnGraphType
                        context.AddField(tableAlias, columnName, ColumnGraphType.Value);
                    }
                    context.CurrentExecutionOperator = operatorChain.Last();
                }
                else if (tableReference is WVariableTableReference)
                {
                    var variableTable = tableReference as WVariableTableReference;
                    var tableName = variableTable.Variable.Name;
                    var tableAlias = variableTable.Alias.Value;
                    Tuple<TemporaryTableHeader, GraphViewExecutionOperator> temporaryTableTuple;
                    if (!context.TemporaryTableCollection.TryGetValue(tableName, out temporaryTableTuple))
                        throw new GraphViewException("Table variable " + tableName + " doesn't exist in the context.");

                    var tableHeader = temporaryTableTuple.Item1;
                    var tableOp = temporaryTableTuple.Item2;
                    operatorChain.Add(operatorChain.Any()
                        ? new CartesianProductOperator2(operatorChain.Last(), tableOp)
                        : tableOp);

                    // Merge temporary table's header into current context
                    foreach (var pair in tableHeader.columnSet.OrderBy(e => e.Value.Item1))
                    {
                        var columnName = pair.Key;
                        var columnGraphType = pair.Value.Item2;

                        context.AddField(tableAlias, columnName, columnGraphType);
                    }
                    context.CurrentExecutionOperator = operatorChain.Last();
                }
                else if (tableReference is WSchemaObjectFunctionTableReference)
                {
                    var functionTableReference = tableReference as WSchemaObjectFunctionTableReference;
                    var functionName = functionTableReference.SchemaObject.Identifiers.ToString();
                    var tableOp = functionTableReference.Compile(context, connection);

                    GraphViewEdgeTableReferenceEnum edgeTypeEnum;
                    GraphViewVertexTableReferenceEnum vertexTypeEnum;
                    if (Enum.TryParse(functionName, true, out edgeTypeEnum))
                        tableReferences.Add(functionTableReference.Alias.Value, TableGraphType.Edge);
                    else if (Enum.TryParse(functionName, true, out vertexTypeEnum))
                        tableReferences.Add(functionTableReference.Alias.Value, TableGraphType.Vertex);
                    // TODO: Change to correct ColumnGraphType
                    else
                        tableReferences.Add(functionTableReference.Alias.Value, TableGraphType.Value);

                    operatorChain.Add(tableOp);
                }
                else
                {

                }

                CheckRemainingPredicatesAndAppendFilterOp(context, connection,
                    new HashSet<string>(tableReferences.Keys), predicatesAccessedTableReferences,
                    operatorChain);
            }

            // TODO: groupBy operator

            if (OrderByClause != null && OrderByClause.OrderByElements != null)
            {
                var orderByOp = OrderByClause.Compile(context, connection);
                operatorChain.Add(orderByOp);
            }

            var projectOperator = new ProjectOperator(operatorChain.Last());
            var selectScalarExprList = SelectElements.Select(e => e as WSelectScalarExpression).ToList();

            if (selectScalarExprList.All(e => e.SelectExpr is WScalarSubquery || e.SelectExpr is WColumnReferenceExpression))
            {
                foreach (var expr in selectScalarExprList)
                {
                    var scalarFunction = expr.SelectExpr.CompileToFunction(context, connection);
                    projectOperator.AddSelectScalarElement(scalarFunction);
                }

                // Rebuild the context's layout
                context.ClearField();
                var i = 0;
                foreach (var expr in selectScalarExprList)
                {
                    var alias = expr.ColumnName;
                    WColumnReferenceExpression columnReference;
                    if (alias == null)
                        columnReference = expr.SelectExpr as WColumnReferenceExpression;
                    else
                        columnReference = new WColumnReferenceExpression("", alias);
                    context.RawRecordLayout.Add(columnReference, i++);
                }
            }
            // TODO: distinguish aggregate function and scalar function from WFunctionCall
            else if (selectScalarExprList.All(e => e.SelectExpr is WFunctionCall))
            {
                throw new NotImplementedException();
            }
            else
            {
                throw new NotImplementedException();
            }

            operatorChain.Add(projectOperator);
            context.CurrentExecutionOperator = projectOperator;

            return operatorChain.Last();
        }


        private GraphViewExecutionOperator ConstructOperator(MatchGraph graph, List<string> header, 
            Dictionary<string, string> columnToAliasDict, GraphViewConnection pConnection, List<BooleanFunction> functions)
        {
            // output and input buffer size is set here.
            const int OUTPUT_BUFFER_SIZE = 50;
            const int INPUT_BUFFER_SIZE = 50;
            List<GraphViewExecutionOperator> ChildrenProcessor = new List<GraphViewExecutionOperator>();
            List<GraphViewExecutionOperator> RootProcessor = new List<GraphViewExecutionOperator>();
            List<string> HeaderForOneOperator = new List<string>();
            // Init function validality cheking list. 
            // Whenever all the operands of a boolean check function appeared, attach the function to the operator.
            List<int> FunctionVaildalityCheck = new List<int>();
            foreach (var i in functions)
            {
                FunctionVaildalityCheck.Add(0);
            }
            int StartOfResult = 0, CurrentMetaHeaderLength = 0;
            // Generate operator for each subgraph.
            foreach (var subgraph in graph.ConnectedSubGraphs)
            {
                var SortedNodes = new Stack<Tuple<MatchNode, MatchEdge>>(subgraph.TraversalChain);
                StartOfResult += subgraph.Nodes.Select(n => n.Value.HeaderLength).Aggregate(0, (cur, next) => cur + next);
                HashSet<MatchNode> ProcessedNode = new HashSet<MatchNode>();
                while (SortedNodes.Count != 0)
                {
                    MatchNode TempNode = null;
                    var CurrentProcessingNode = SortedNodes.Pop();
                    // If a node is a source node and never appeared before, it will be consturcted to a fetchnode operator
                    // Otherwise it will be consturcted to a TraversalOperator.
                    if (!ProcessedNode.Contains(CurrentProcessingNode.Item1))
                    {
                        int node = header.IndexOf(CurrentProcessingNode.Item1.NodeAlias);
                        TempNode = CurrentProcessingNode.Item1;
                        CurrentMetaHeaderLength += TempNode.HeaderLength;
                        HeaderForOneOperator = header.GetRange(0, CurrentMetaHeaderLength);

                        for (int i = StartOfResult; i < header.Count; i++)
                            HeaderForOneOperator.Add(header[i]);
                        //if (ChildrenProcessor.Count == 0)
                        ChildrenProcessor.Add(new FetchNodeOperator(pConnection, CurrentProcessingNode.Item1.AttachedQuerySegment, node, HeaderForOneOperator, TempNode.HeaderLength, 50));
                        //else
                        //    ChildrenProcessor.Add(new FetchNodeOperator(pConnection, CurrentProcessingNode.Item1.AttachedQuerySegment, node, HeaderForOneOperator, ProcessedNode.Count, 50, ChildrenProcessor.Last()));
                        if (functions != null && functions.Count != 0)
                            CheckFunctionValidate(ref header, ref functions, ref TempNode, ref FunctionVaildalityCheck, ref ChildrenProcessor);
                        ProcessedNode.Add(CurrentProcessingNode.Item1);

                    }
                    if (CurrentProcessingNode.Item2 != null)
                    {
                        TempNode = CurrentProcessingNode.Item2.SinkNode;

                        int src = header.IndexOf(CurrentProcessingNode.Item2.SourceNode.NodeAlias);
                        int srcAdj = header.IndexOf(CurrentProcessingNode.Item2.EdgeAlias + "_ADJ");
                        int dest = header.IndexOf(CurrentProcessingNode.Item2.SinkNode.NodeAlias);

                        CurrentMetaHeaderLength += TempNode.HeaderLength;
                        HeaderForOneOperator = header.GetRange(0, CurrentMetaHeaderLength);

                        for (int i = StartOfResult; i < header.Count; i++)
                            HeaderForOneOperator.Add(header[i]);

                        Tuple<string, GraphViewExecutionOperator, int> InternalOperator = null;
                        if (WithPathClause != null && (InternalOperator =
                                    WithPathClause.PathOperators.Find(
                                        p => p.Item1 == CurrentProcessingNode.Item2.EdgeAlias)) !=
                                null)
                        {
                            // if WithPathClause != null, internal operator should be constructed for the traversal operator that deals with path.
                            ChildrenProcessor.Add(new TraversalOperator(pConnection, ChildrenProcessor.Last(),
                                TempNode.AttachedQuerySegment, src, srcAdj, dest, HeaderForOneOperator,
                                TempNode.HeaderLength, TempNode.ReverseCheckList, INPUT_BUFFER_SIZE,
                                OUTPUT_BUFFER_SIZE, false, InternalOperator.Item2));
                        }
                        else
                        {
                            ChildrenProcessor.Add(new TraversalOperator(pConnection, ChildrenProcessor.Last(),
                                TempNode.AttachedQuerySegment, src, srcAdj, dest, HeaderForOneOperator,
                                TempNode.HeaderLength, TempNode.ReverseCheckList, INPUT_BUFFER_SIZE,
                                OUTPUT_BUFFER_SIZE, CurrentProcessingNode.Item2.IsReversed));
                        }
                        ProcessedNode.Add(TempNode);
                        // Check if any boolean function should be attached to this operator.
                        if (functions != null && functions.Count != 0)
                            CheckFunctionValidate(ref header, ref functions, ref TempNode, ref FunctionVaildalityCheck, ref ChildrenProcessor);
                    }

                }
                // The last processor of a sub graph will be added to root processor list for later use.
                RootProcessor.Add(ChildrenProcessor.Last());

                for (int i = 0; i < FunctionVaildalityCheck.Count; i++)
                    if (FunctionVaildalityCheck[i] == 1) FunctionVaildalityCheck[i] = 0;
            }
            GraphViewExecutionOperator root = null;
            if (RootProcessor.Count == 1) root = RootProcessor[0];
            // A cartesian product will be made among all the result from the root processor in order to produce a complete result
            else
            {
                root = new CartesianProductOperator(RootProcessor, header);
                // If some boolean function cannot be attached in any single subgraph, it should either be attached to cartesian product operator.
                // or it cannot be attached anywhere.
                for (int i = 0; i < FunctionVaildalityCheck.Count; i++)
                {
                    if (FunctionVaildalityCheck[i] < 2)
                    {
                        if ((root as CartesianProductOperator).BooleanCheck == null)
                            (root as CartesianProductOperator).BooleanCheck = functions[i];
                        else (root as CartesianProductOperator).BooleanCheck = new BooleanBinaryFunction((root as CartesianProductOperator).BooleanCheck,
                                        functions[i], BooleanBinaryFunctionType.And);
                    }
                }
            }
            if (OrderByClause != null && OrderByClause.OrderByElements != null)
            {
                var orderByElements = new List<Tuple<string, SortOrder>>();
                foreach (var element in OrderByClause.OrderByElements)
                {
                    var sortOrder = element.SortOrder;
                    var expr = element.ScalarExpr.ToString();
                    string alias;
                    // if expr is a column name with an alias, use its alias
                    if (columnToAliasDict.TryGetValue(expr, out alias))
                        orderByElements.Add(new Tuple<string, SortOrder>(alias, sortOrder));
                    // if expr is already the alias, use the expr directly
                    else if (columnToAliasDict.ContainsValue(expr))
                        orderByElements.Add(new Tuple<string, SortOrder>(expr, sortOrder));
                    else
                        throw new GraphViewException(string.Format("Invalid column name '{0}'", expr));
                }
                //(from wExpressionWithSortOrder in OrderByClause.OrderByElements
                //    let expr = columnToAliasDict[wExpressionWithSortOrder.ScalarExpr.ToString()]
                //    let sortOrder = wExpressionWithSortOrder.SortOrder
                //    select new Tuple<string, SortOrder>(expr, sortOrder)).ToList();
                root = new OrderbyOperator(root, orderByElements, header);
            }

            List<string> SelectedElement = new List<string>();
            foreach (var x in SelectElements)
            {
                var expr = (x as WSelectScalarExpression).SelectExpr;
                if (expr is WColumnReferenceExpression)
                {
                    var columnName = (expr as WColumnReferenceExpression).MultiPartIdentifier.ToString();
                    string alias;
                    columnToAliasDict.TryGetValue(columnName, out alias);
                    if (alias == null) alias = columnName;
                    SelectedElement.Add(alias);
                }
            }
            if (!OutputPath)
                root = new OutputOperator(root, SelectedElement, root.header);
            else
                root = new OutputOperator(root, true, header);
            return root;
        }

        private void BuildQuerySegementOnNode(List<string> ProcessedNodeList, MatchNode node, List<string> header, 
            Dictionary<string, List<Tuple<MatchEdge, MaterializedEdgeType>>> nodeToMatEdgesDict, Dictionary<string, string> columnToAliasDict, Dictionary<string, DColumnReferenceExpression> headerToColumnRefDict,
            int pStartOfResultField, bool isPathTailNode = false)
        {
            DFromClause fromClause = new DFromClause();
            string FromClauseString = "";
            WBooleanExpression searchCondition = null;

            string scriptBase = string.Format("SELECT {{\"id\":{0}.id}} AS _nodeid", node.NodeAlias);
            const string edgeProjectBase = "{{\"_sink\": {0}._sink, \"_ID\": {0}._ID";
            // <edge, extra properties need to be pulled from the server besides _sink and _ID>
            var edgeProjection = new Dictionary<string, List<DColumnReferenceExpression>>();

            fromClause.TableReference = node.NodeAlias;

            if (!isPathTailNode && nodeToMatEdgesDict != null)
            {
                // Join every edge needs to be pulled
                foreach (var t in nodeToMatEdgesDict[node.NodeAlias])
                {
                    var edge = t.Item1;
                    FromClauseString += " Join " + edge.EdgeAlias + " in " + node.NodeAlias + 
                        (edge.IsReversed
                        ? "._reverse_edge "
                        : "._edge ");
                    // Add all the predicates on edges to the where clause.
                    foreach (var predicate in edge.Predicates)
                        searchCondition = WBooleanBinaryExpression.Conjunction(searchCondition, predicate);
                    edgeProjection[edge.EdgeAlias] = new List<DColumnReferenceExpression>();
                }
            }
            fromClause.FromClauseString = FromClauseString;

            // Add all the predicates on nodes to the where clause.
            foreach (var predicate in node.Predicates)
                searchCondition = WBooleanBinaryExpression.Conjunction(searchCondition, predicate);

            // Select elements that related to current node and its edges will be attached here.
            List<DColumnReferenceExpression> DSelectElements = new List<DColumnReferenceExpression>();
            for (var i = pStartOfResultField; i < header.Count; i++)
            {
                var str = header[i];
                int CutPoint = str.Length;
                if (str.IndexOf('.') != -1) CutPoint = str.IndexOf('.');
                if (str.Substring(0, CutPoint) == node.NodeAlias)
                {
                    // Replace the column name in header with its alias
                    header[i] = columnToAliasDict[str];
                    DSelectElements.Add(headerToColumnRefDict[str]);
                }
                if (nodeToMatEdgesDict != null)
                {
                    foreach (var t in nodeToMatEdgesDict[node.NodeAlias])
                    {
                        var edge = t.Item1;
                        if (str.Substring(0, CutPoint) == edge.EdgeAlias)
                        {
                            header[i] = columnToAliasDict[str];
                            edgeProjection[edge.EdgeAlias].Add(headerToColumnRefDict[str]);
                        }
                    }
                }
            }

            // Reverse checking pair generation
            if (!isPathTailNode && nodeToMatEdgesDict != null)
            {
                foreach (var t in nodeToMatEdgesDict[node.NodeAlias])
                {
                    var edge = t.Item1;
                    // <index of adj field, index of dest id field>
                    if (ProcessedNodeList.Contains(edge.SinkNode.NodeAlias))
                        node.ReverseCheckList.Add(header.IndexOf(edge.EdgeAlias + "_ADJ"), header.IndexOf(edge.SinkNode.NodeAlias));
                    else
                        edge.SinkNode.ReverseCheckList.Add(header.IndexOf(edge.EdgeAlias + "_ADJ"), header.IndexOf(edge.SinkNode.NodeAlias));
                }
            }

            foreach (var pair in edgeProjection)
            {
                var edgeAlias = pair.Key;
                var projects = pair.Value;
                scriptBase += ", " + string.Format(edgeProjectBase, edgeAlias);
                scriptBase = projects.Aggregate(scriptBase,
                    (current, project) =>
                        current + ", " +
                        string.Format("\"{0}\": {1}", project.ColumnName, project.MultiPartIdentifier.ToString()));
                scriptBase += string.Format("}} AS {0}_ADJ", edgeAlias);
            }

            // The DocDb script of the current node will be assembled here.
            WWhereClause whereClause = new WWhereClause {SearchCondition = searchCondition};
            DocDbScript script = new DocDbScript {ScriptBase = scriptBase, SelectElements = DSelectElements, FromClause = fromClause, WhereClause = whereClause, OriginalSearchCondition = searchCondition};
            node.AttachedQuerySegment = script;
        }

        // Check if any operand of the boolean functions appeared in the operator, increase the corresponding mark if so.
        // Whenever all the operands of a boolean check function appeared, attach the function to the operator.
        private void CheckFunctionValidate(ref List<string> header, ref List<BooleanFunction> functions, ref MatchNode TempNode, ref List<int> FunctionVaildalityCheck, ref List<GraphViewExecutionOperator> ChildrenProcessor)
        {
            for (int i = 0; i < functions.Count; i++)
            {
                if (functions[i] is FieldComparisonFunction)
                {
                    //string lhs = header[(functions[i] as FieldComparisonFunction).LhsFieldIndex];
                    //string rhs = header[(functions[i] as FieldComparisonFunction).RhsFieldIndex];
                    string lhs = (functions[i] as FieldComparisonFunction).LhsFieldName;
                    string rhs = (functions[i] as FieldComparisonFunction).RhsFieldName;
                    bool isLhsContained = false, isRhsContained = false;
                    var selectElements =
                        TempNode.AttachedQuerySegment.SelectElements.Select(expr => expr.ToSqlStyleString()).ToList();

                    foreach (var expr in selectElements)
                    {
                        if (expr.Contains(lhs))
                            isLhsContained = true;
                        if (expr.Contains(rhs))
                            isRhsContained = true;
                    }
                    if (isLhsContained)
                        FunctionVaildalityCheck[i]++;
                    if (isRhsContained)
                        FunctionVaildalityCheck[i]++;

                    if (FunctionVaildalityCheck[i] == 2)
                        {
                            functions[i].header = ChildrenProcessor.Last().header;
                            if (ChildrenProcessor.Last() != null && ChildrenProcessor.Last() is TraversalBaseOperator)
                            {
                                if ((ChildrenProcessor.Last() as TraversalBaseOperator).crossDocumentJoinPredicates ==
                                    null)
                                    (ChildrenProcessor.Last() as TraversalBaseOperator).crossDocumentJoinPredicates =
                                        functions[i];
                                else
                                    (ChildrenProcessor.Last() as TraversalBaseOperator).crossDocumentJoinPredicates =
                                        new BooleanBinaryFunction(
                                            (ChildrenProcessor.Last() as TraversalBaseOperator)
                                                .crossDocumentJoinPredicates,
                                            functions[i], BooleanBinaryFunctionType.And);
                            }
                            FunctionVaildalityCheck[i] = 0;
                        }
                }
            }
        }

        //private List<Tuple<int, string>> ConsturctReverseCheckList(MatchNode TempNode, ref HashSet<MatchNode> ProcessedNode, List<string> header)
        //{
        //    List<Tuple<int, string>> ReverseCheckList = new List<Tuple<int, string>>();
        //    foreach (var neighbor in TempNode.ReverseNeighbors)
        //        if (ProcessedNode.Contains(neighbor.SinkNode))
        //            ReverseCheckList.Add(new Tuple<int, string>(header.IndexOf(neighbor.SinkNode.NodeAlias),
        //                neighbor.EdgeAlias + "_REV"));
        //    foreach (var neighbor in TempNode.Neighbors)
        //        if (ProcessedNode.Contains(neighbor.SinkNode))
        //            ReverseCheckList.Add(new Tuple<int, string>(header.IndexOf(neighbor.SinkNode.NodeAlias),
        //                neighbor.EdgeAlias + "_REV"));
        //    return ReverseCheckList;
        //}

        // Cut the last character of a string.
        private string CutTheTail(string InRangeScript)
        {
            if (InRangeScript.Length == 0) return "";
            return InRangeScript.Substring(0, InRangeScript.Length - 1);
        }
        // The implementation of Union find algorithmn.
        private class UnionFind
        {
            public Dictionary<string, string> Parent;

            public string Find(string x)
            {
                string k, j, r;
                r = x;
                while (Parent[r] != r)
                {
                    r = Parent[r];
                }
                k = x;
                while (k != r)
                {
                    j = Parent[k];
                    Parent[k] = r;
                    k = j;
                }
                return r;
            }

            public void Union(string a, string b)
            {
                string aRoot = Find(a);
                string bRoot = Find(b);
                if (aRoot == bRoot)
                    return;
                Parent[aRoot] = bRoot;
            }
        }

        // The implementation of topological sorting using DFS
        // Note that if is there's a cycle, a random node in the cycle will be pick as the start.
        private class TopoSorting
        {
            static internal Stack<Tuple<MatchNode, MatchEdge>> TopoSort(Dictionary<string, MatchNode> graph)
            {
                Dictionary<MatchNode, int> state = new Dictionary<MatchNode, int>();
                Stack<Tuple<MatchNode, MatchEdge>> list = new Stack<Tuple<MatchNode, MatchEdge>>();
                foreach (var node in graph)
                    state.Add(node.Value, 0);
                foreach (var node in graph)
                    if (state[node.Value] == 0)
                        visit(graph, node.Value, list, state, node.Value.NodeAlias, null);
                if (graph.Count == 1) list.Push(new Tuple<MatchNode, MatchEdge>(graph.First().Value, null));
                return list;
            }
            static private void visit(Dictionary<string, MatchNode> graph, MatchNode node, Stack<Tuple<MatchNode, MatchEdge>> list, Dictionary<MatchNode, int> state, string ParentAlias, MatchEdge Edge)
            {
                state[node] = 2;
                foreach (var neighbour in node.Neighbors)
                {
                    if (state[neighbour.SinkNode] == 0)
                        visit(graph, neighbour.SinkNode, list, state, node.NodeAlias, neighbour);
                    if (state[neighbour.SinkNode] == 2)
                        foreach (var neighbour2 in neighbour.SinkNode.ReverseNeighbors)
                        {
                            foreach (var x in neighbour2.SinkNode.Neighbors)
                                if (x.SinkNode == node)
                                    list.Push(new Tuple<MatchNode, MatchEdge>(x.SourceNode, x));
                        }

                }
                state[node] = 1;
                foreach (var neighbour in node.ReverseNeighbors)
                {
                    foreach (var x in neighbour.SinkNode.Neighbors)
                        if (x.SinkNode == node)
                            list.Push(new Tuple<MatchNode, MatchEdge>(x.SourceNode, x));
                }
            }
        }
    }

    partial class WWithPathClause
    {
        internal override GraphViewExecutionOperator Generate(GraphViewConnection dbConnection)
        {
            foreach (var path in Paths)
            {
                //path.Item2.SelectElements = new List<WSelectElement>();
                PathOperators.Add(new Tuple<string, GraphViewExecutionOperator, int>(path.Item1,
                    path.Item2.Generate(dbConnection), path.Item3));
            }
            if (PathOperators.Count != 0) return PathOperators.First().Item2;
            else return null;
        }
    }

    partial class WChoose
    {
        internal override GraphViewExecutionOperator Generate(GraphViewConnection dbConnection)
        {
            List<GraphViewExecutionOperator> Source = new List<GraphViewExecutionOperator>();
            foreach (var x in InputExpr)
            {
                Source.Add(x.Generate(dbConnection));
            }
            return new ConcatenateOperator(Source);
        }
    }

    partial class WCoalesce
    {
        internal override GraphViewExecutionOperator Generate(GraphViewConnection dbConnection)
        {
            List<GraphViewExecutionOperator> Source = new List<GraphViewExecutionOperator>();
            foreach (var x in InputExpr)
            {
                Source.Add(x.Generate(dbConnection));
            }
            var op = new CoalesceOperator(Source, CoalesceNumber);
            return new OutputOperator(op, op.header, null);
        }
    }

    partial class WSqlBatch
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewConnection dbConnection)
        {
            QueryCompilationContext priorContext = new QueryCompilationContext();
            GraphViewExecutionOperator op = null;
            foreach (WSqlStatement st in Statements)
            {
                QueryCompilationContext statementContext = new QueryCompilationContext(priorContext.TemporaryTableCollection);
                op = st.Compile(statementContext, dbConnection);
                priorContext = statementContext;
            }

            // Returns the last execution operator
            // To consider: prior execution operators that have no links to the last operator will not be executed.
            return op;
        }
    }

    partial class WSetVariableStatement
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewConnection dbConnection)
        {
            if (_expression.GetType() != typeof(WScalarSubquery))
            {
                throw new NotImplementedException();
            }

            WSqlStatement subquery = (_expression as WScalarSubquery).SubQueryExpr;
            GraphViewExecutionOperator subqueryOp = subquery.Compile(context, dbConnection);
            TemporaryTableHeader tmpTableHeader = context.ToTableHeader();
            // Adds the table populated by the statement as a temporary table to the context
            context.TemporaryTableCollection[_variable.Name] = new Tuple<TemporaryTableHeader, GraphViewExecutionOperator>(tmpTableHeader, subqueryOp);

            return subqueryOp;
        }
    }

    partial class WOrderByClause
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewConnection dbConnection)
        {

            var orderByElements = new List<Tuple<int, SortOrder>>();
            if (OrderByElements != null)
            {
                foreach (var element in OrderByElements)
                {
                    var expr = element.ScalarExpr as WColumnReferenceExpression;
                    if (expr == null)
                        throw new SyntaxErrorException("The order by elements can only be WColumnReferenceExpression.");

                    orderByElements.Add(new Tuple<int, SortOrder>(context.LocateColumnReference(expr), element.SortOrder));
                }
            }

            var orderByOp = new OrderbyOperator2(context.CurrentExecutionOperator, orderByElements);
            context.CurrentExecutionOperator = orderByOp;
            return orderByOp;
        }
    }

    partial class WCoalesceTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewConnection dbConnection)
        {
            CoalesceOperator2 coalesceOp = new CoalesceOperator2(context.CurrentExecutionOperator);

            WSelectQueryBlock firstSelectQuery = null;
            foreach (WScalarExpression parameter in Parameters)
            {
                WScalarSubquery scalarSubquery = parameter as WScalarSubquery;
                if (scalarSubquery == null)
                {
                    throw new SyntaxErrorException("The input of a coalesce table reference must be one or more scalar subqueries.");
                }

                if (firstSelectQuery == null)
                {
                    firstSelectQuery = scalarSubquery.SubQueryExpr as WSelectQueryBlock;
                    if (firstSelectQuery == null)
                    {
                        throw new SyntaxErrorException("The input of a coalesce table reference must be one or more select query blocks.");
                    }
                }

                QueryCompilationContext subcontext = new QueryCompilationContext(context);
                GraphViewExecutionOperator traversalOp = scalarSubquery.SubQueryExpr.Compile(subcontext, dbConnection);
                coalesceOp.AddTraversal(subcontext.OuterContextOp, traversalOp);
            }

            // Updates the raw record layout. The columns of this table-valued function 
            // are specified by the select elements of the input subqueries.
            foreach (WSelectElement selectElement in firstSelectQuery.SelectElements)
            {
                WSelectScalarExpression selectScalar = selectElement as WSelectScalarExpression;
                if (selectScalar == null)
                {
                    throw new SyntaxErrorException("The input subquery of a coalesce table reference can only select scalar elements.");
                }
                WColumnReferenceExpression columnRef = selectScalar.SelectExpr as WColumnReferenceExpression;
                if (columnRef == null)
                {
                    throw new SyntaxErrorException("The input subquery of a coalesce table reference can only select column epxressions.");
                }
                context.AddField(Alias.Value, columnRef.ColumnName, columnRef.ColumnGraphType);
            }

            context.CurrentExecutionOperator = coalesceOp;
            return coalesceOp;
        }
    }

    partial class WOptionalTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewConnection dbConnection)
        {
            WSelectQueryBlock contextSelect, optionalSelect;
            Split(out contextSelect, out optionalSelect);

            List<int> inputIndexes = new List<int>();
            List<WColumnReferenceExpression> columnList = new List<WColumnReferenceExpression>();

            foreach (WSelectElement selectElement in contextSelect.SelectElements)
            {
                WSelectScalarExpression selectScalar = selectElement as WSelectScalarExpression;
                if (selectScalar == null)
                {
                    throw new SyntaxErrorException("The SELECT elements of the sub-queries in an optional table reference must be select scalar elements.");
                }
                WColumnReferenceExpression columnRef = selectScalar.SelectExpr as WColumnReferenceExpression;
                if (columnRef == null)
                {
                    throw new SyntaxErrorException("The SELECT elements of the sub-queries in an optional table reference must be column references.");
                }

                int index = context.LocateColumnReference(columnRef);
                inputIndexes.Add(index);

                columnList.Add(columnRef);
            }

            QueryCompilationContext subcontext = new QueryCompilationContext(context);
            GraphViewExecutionOperator optionalTraversalOp = optionalSelect.Compile(subcontext, dbConnection);

            OptionalOperator optionalOp = new OptionalOperator(context.CurrentExecutionOperator, inputIndexes, optionalTraversalOp, subcontext.OuterContextOp);
            context.CurrentExecutionOperator = optionalOp;

            // Updates the raw record layout. The columns of this table-valued function 
            // are specified by the select elements of the input subqueries.
            foreach (WColumnReferenceExpression columnRef in columnList)
            {
                context.AddField(Alias.Value, columnRef.ColumnName, columnRef.ColumnGraphType);
            }

            return optionalOp;
        }
    }

    partial class WLocalTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewConnection dbConnection)
        {
            WScalarSubquery localSubquery = Parameters[0] as WScalarSubquery;
            if (localSubquery == null)
            {
                throw new SyntaxErrorException("The input of a local table reference must be a scalar subquery.");
            }
            WSelectQueryBlock localSelect = localSubquery.SubQueryExpr as WSelectQueryBlock;
            if (localSelect == null)
            {
                throw new SyntaxErrorException("The sub-query must be a select query block.");
            }

            foreach (WSelectElement selectElement in localSelect.SelectElements)
            {
                WSelectScalarExpression selectScalar = selectElement as WSelectScalarExpression;
                if (selectScalar == null)
                {
                    throw new SyntaxErrorException("The SELECT elements of the sub-query in a local table reference must be select scalar elements.");
                }
                WColumnReferenceExpression columnRef = selectScalar.SelectExpr as WColumnReferenceExpression;
                if (columnRef == null)
                {
                    throw new SyntaxErrorException("The SELECT elements of the sub-query in a local table reference must be column references.");
                }
                context.AddField(Alias.Value, columnRef.ColumnName, columnRef.ColumnGraphType);
            }

            QueryCompilationContext subcontext = new QueryCompilationContext(context);
            GraphViewExecutionOperator localTraversalOp = localSelect.Compile(subcontext, dbConnection);

            LocalOperator localOp = new LocalOperator(context.CurrentExecutionOperator, localTraversalOp, subcontext.OuterContextOp);
            context.CurrentExecutionOperator = localOp;

            return localOp;
        }
    }

    partial class WFlatMapTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewConnection dbConnection)
        {
            WScalarSubquery localSubquery = Parameters[0] as WScalarSubquery;
            if (localSubquery == null)
            {
                throw new SyntaxErrorException("The input of a flatMap table reference must be a scalar subquery.");
            }
            WSelectQueryBlock flatMapSelect = localSubquery.SubQueryExpr as WSelectQueryBlock;
            if (flatMapSelect == null)
            {
                throw new SyntaxErrorException("The sub-query must be a select query block.");
            }

            foreach (WSelectElement selectElement in flatMapSelect.SelectElements)
            {
                WSelectScalarExpression selectScalar = selectElement as WSelectScalarExpression;
                if (selectScalar == null)
                {
                    throw new SyntaxErrorException("The SELECT elements of the sub-query in a flatMap table reference must be select scalar elements.");
                }
                WColumnReferenceExpression columnRef = selectScalar.SelectExpr as WColumnReferenceExpression;
                if (columnRef == null)
                {
                    throw new SyntaxErrorException("The SELECT elements of the sub-query in a flatMap table reference must be column references.");
                }
                context.AddField(Alias.Value, columnRef.ColumnName, columnRef.ColumnGraphType);
            }

            QueryCompilationContext subcontext = new QueryCompilationContext(context);
            GraphViewExecutionOperator flatMapTraversalOp = flatMapSelect.Compile(subcontext, dbConnection);

            LocalOperator flatMapOp = new LocalOperator(context.CurrentExecutionOperator, flatMapTraversalOp, subcontext.OuterContextOp);
            context.CurrentExecutionOperator = flatMapOp;

            return flatMapOp;
        }
    }

    partial class WBoundOutNodeTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewConnection dbConnection)
        {
            var sinkParameter = Parameters[0] as WColumnReferenceExpression;
            var sinkIndex = context.LocateColumnReference(sinkParameter);
            var nodeAlias = Alias.Value;
            var isSendQueryRequired = !(Parameters.Count == 2 && (Parameters[1] as WValueExpression).Value.Equals("id"));
            var matchNode = new MatchNode
            {
                AttachedJsonQuery = null,
                NodeAlias = nodeAlias,
                Predicates = new List<WBooleanExpression>(),
                Properties = new List<string>{ "id", "_edge", "_reverse_edge" },
            };

            if (isSendQueryRequired)
            {
                for (int i = 1; i < Parameters.Count; i++)
                {
                    var property = (Parameters[i] as WValueExpression).Value;
                    if (!matchNode.Properties.Contains(property))
                        matchNode.Properties.Add(property);
                }
                WSelectQueryBlock.ConstructJsonQueryOnNode(matchNode);

                // TODO: Change to correct ColumnGraphType
                foreach (var property in matchNode.Properties)
                    context.AddField(nodeAlias, property, ColumnGraphType.Value);
            }
            else
            {
                context.AddField(nodeAlias, "id", ColumnGraphType.VertexId);
            }

            var traversalOp = new TraversalOperator2(context.CurrentExecutionOperator, dbConnection, sinkIndex,
                matchNode.AttachedJsonQuery, null);
            context.CurrentExecutionOperator = traversalOp;

            return traversalOp;
        }
    }

    partial class WBoundBothNodeTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewConnection dbConnection)
        {
            var firstSinkParameter = Parameters[0] as WColumnReferenceExpression;
            var secondSinkParameter = Parameters[1] as WColumnReferenceExpression;
            var sinkIndexes = new List<int>
            {
                context.LocateColumnReference(firstSinkParameter),
                context.LocateColumnReference(secondSinkParameter)
            };
            var nodeAlias = Alias.Value;
            var isSendQueryRequired = !(Parameters.Count == 3 && (Parameters[2] as WValueExpression).Value.Equals("id"));
            var matchNode = new MatchNode
            {
                AttachedJsonQuery = null,
                NodeAlias = nodeAlias,
                Predicates = new List<WBooleanExpression>(),
                Properties = new List<string> { "id", "_edge", "_reverse_edge" },
            };

            if (isSendQueryRequired)
            {
                for (int i = 2; i < Parameters.Count; i++)
                {
                    var property = (Parameters[i] as WValueExpression).Value;
                    if (!matchNode.Properties.Contains(property))
                        matchNode.Properties.Add(property);
                }
                WSelectQueryBlock.ConstructJsonQueryOnNode(matchNode);

                // TODO: Change to correct ColumnGraphType
                foreach (var property in matchNode.Properties)
                    context.AddField(nodeAlias, property, ColumnGraphType.Value);
            }
            else
            {
                context.AddField(nodeAlias, "id", ColumnGraphType.VertexId);
            }

            var bothVOp = new BothVOperator(context.CurrentExecutionOperator, dbConnection, sinkIndexes,
                matchNode.AttachedJsonQuery);
            context.CurrentExecutionOperator = bothVOp;

            return bothVOp;
        }
    }

    partial class WBoundOutEdgeTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context,
            GraphViewConnection dbConnection)
        {
            var adjListParameter = Parameters[0] as WColumnReferenceExpression;
            var adjListIndex = context.LocateColumnReference(adjListParameter);
            var edgeAlias = Alias.Value;
            var projectFields = new List<string> { "_sink" };

            for (int i = 1; i < Parameters.Count; i++)
            {
                var field = (Parameters[i] as WValueExpression).Value;
                if (!projectFields.Contains(field))
                    projectFields.Add(field);
            }

            foreach (var projectField in projectFields)
            {
                // TODO: Change to correct ColumnGraphType
                context.AddField(edgeAlias, projectField, ColumnGraphType.Value);
            }

            var adjListDecoder = new AdjacencyListDecoder(context.CurrentExecutionOperator, new List<int> {adjListIndex},
                null, projectFields);
            context.CurrentExecutionOperator = adjListDecoder;

            return adjListDecoder;
        }
    }

    partial class WBoundBothEdgeTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewConnection dbConnection)
        {
            var firstAdjListParameter = Parameters[0] as WColumnReferenceExpression;
            var secondAdjListParameter = Parameters[1] as WColumnReferenceExpression;
            var adjListIndexes = new List<int>
            {
                context.LocateColumnReference(firstAdjListParameter),
                context.LocateColumnReference(secondAdjListParameter)
            };
            var edgeAlias = Alias.Value;
            var projectFields = new List<string> { "_sink" };

            for (int i = 2; i < Parameters.Count; i++)
            {
                var field = (Parameters[i] as WValueExpression).Value;
                if (!projectFields.Contains(field))
                    projectFields.Add(field);
            }

            foreach (var projectField in projectFields)
            {
                // TODO: Change to correct ColumnGraphType
                context.AddField(edgeAlias, projectField, ColumnGraphType.Value);
            }

            var adjListDecoder = new AdjacencyListDecoder(context.CurrentExecutionOperator, adjListIndexes,
                null, projectFields);
            context.CurrentExecutionOperator = adjListDecoder;

            return adjListDecoder;
        }
    }

    partial class WValuesTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewConnection dbConnection)
        {
            List<int> valuesIdxList = new List<int>();

            foreach (var expression in Parameters)
            {
                var columnReference = expression as WColumnReferenceExpression;
                if (columnReference == null)
                    throw new SyntaxErrorException("Parameters of Values function can only be WColumnReference.");
                valuesIdxList.Add(context.LocateColumnReference(columnReference));
            }

            GraphViewExecutionOperator valuesOperator = new ValuesOperator(context.CurrentExecutionOperator, valuesIdxList);
            context.CurrentExecutionOperator = valuesOperator;
            context.AddField(Alias.Value, "_value", ColumnGraphType.Value);
            
            return valuesOperator;
        }
    }

    partial class WPropertiesTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewConnection dbConnection)
        {
            List<Tuple<string, int>> propertiesList = new List<Tuple<string, int>>();

            foreach (var expression in Parameters)
            {
                var columnReference = expression as WColumnReferenceExpression;
                if (columnReference == null)
                    throw new SyntaxErrorException("Parameters of Values function can only be WColumnReference.");
                propertiesList.Add(new Tuple<string, int>(columnReference.ColumnName,
                    context.LocateColumnReference(columnReference)));
            }

            GraphViewExecutionOperator propertiesOp = new PropertiesOperator(context.CurrentExecutionOperator, propertiesList);
            context.CurrentExecutionOperator = propertiesOp;
            context.AddField(Alias.Value, "_value", ColumnGraphType.Value);

            return propertiesOp;
        }
    }
}


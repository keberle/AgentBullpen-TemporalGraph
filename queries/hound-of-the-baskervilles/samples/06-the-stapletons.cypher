// Who are the Stapletons connected to?
// RELATED_TO and IDENTICAL_TO edges of every character whose name contains 'Stapleton'. An identity (one person under two names) shows only once the text has revealed it.
// Parameters: $pos (the timeline's chapter end), $spoilers. visible(x) is the spoiler rule on a node or an edge.
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids), hidden in the results grid.
MATCH (a:Character)-[r:RELATED_TO|IDENTICAL_TO]-(b) WHERE toLower(a.name) CONTAINS 'stapleton' AND visible(a) AND visible(r) AND visible(b)
RETURN a.name AS character, b.name AS other, coalesce(r.kind, 'the same person') AS kind, r.revealedPos AS revealed, id(b) AS otherId, id(r) AS edgeId
ORDER BY r.revealedPos

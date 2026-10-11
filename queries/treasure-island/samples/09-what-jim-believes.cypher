// What does Jim Hawkins believe, doubt or fear?
// Jim Hawkins's PERCEIVES edges: the kind, the fact or person, and since when in story time. A perception that changed is closed and a new one opens.
// Parameters: $pos (the timeline's chapter end), $spoilers. visible(x) is the spoiler rule on a node or an edge.
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids), hidden in the results grid.
MATCH (c:Character)-[r:PERCEIVES]->(t) WHERE 'Jim' IN c.aliases AND visible(c) AND visible(r) AND visible(t)
RETURN r.kind AS kind, coalesce(t.text, t.name) AS target, r.storyFrom AS since, r.revealedPos AS revealed, id(t) AS targetId, id(r) AS edgeId ORDER BY r.revealedPos

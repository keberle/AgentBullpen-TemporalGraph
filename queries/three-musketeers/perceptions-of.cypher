// perceptions_of(character, target, pos): how a character takes a fact or another character: believes, doubts, disbelieves, trusts, distrusts,
// suspects, threatened, reassured, fearful, hopeful, curious, indifferent. What a character knows is the facts it perceives, except those it disbelieves.
// Parameters: $character, $target (a name: the perceived character, or someone the perceived facts are about; null for all), $pos, $spoilers.
// visible(x) is the spoiler rule on a node or an edge (Spoilers off: revealed by $pos and not retracted; Spoilers on: true in story time by story($pos)).
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids) for the chat tools, which strip them (the graph highlight).
MATCH (c:Character)-[r:PERCEIVES]->(t) WHERE $character IN c.aliases AND visible(c) AND visible(r) AND visible(t)
OPTIONAL MATCH (s) WHERE id(s) IN t.subject AND ($target IS NULL OR $target IN s.aliases)
WITH r, t, collect(s.name) AS about, collect(id(s)) AS aboutIds, count(s) AS hits
WHERE $target IS NULL OR $target IN t.aliases OR hits > 0
RETURN r.kind AS kind, coalesce(t.text, t.name) AS target, about, r.storyFrom AS since, r.revealedPos AS revealed, id(t) AS targetId, aboutIds, id(r) AS edgeId
ORDER BY r.revealedPos

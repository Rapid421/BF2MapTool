"""The project chain: every stage that edits the level loads the project of the stage before it and saves its own
(projects/<stage>.fbproject). One table, so adding a stage does not touch its neighbours."""
ORDER = ['shell', 'shaderdb', 'import', 'place', 'doors', 'lights', 'look', 'navmesh', 'spawns', 'kill', 'bake', 'supremacy', 'audit']
OPTIONAL = ('bake', 'supremacy')             # stages that are part of a build only when the job asks for them (job.wants)


def base(job, stage):
    """the project a stage starts from: the stage before it that is part of this job's build"""
    i = ORDER.index(stage) - 1
    while ORDER[i] in OPTIONAL and not job.wants(ORDER[i]):
        i -= 1
    return job.project(ORDER[i])


def prev(stage):
    return ORDER[ORDER.index(stage) - 1]

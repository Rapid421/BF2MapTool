"""The port pipeline: stage order and dependencies. A stage is redone when its own inputs or any stage it depends on
changed (runner.plan). Stages that edit the level form a chain of projects (chain.ORDER)."""
from .runner import Stage
from .stages import bake, collision, doors, finish, importer, kill, lights, look, nav, shaderdb, shell, spawns, split, supremacy, textures


def port_stages(with_bake=False, with_supremacy=False):
    st = [
        Stage('split', split.run, inputs=split.inputs, outputs=split.outputs, title='Split into pieces'),
        Stage('textures', textures.run, deps=('split',), inputs=textures.inputs, outputs=textures.outputs, title='Pack textures'),
        Stage('collision', collision.run, deps=('split',), inputs=collision.inputs, outputs=collision.outputs, title='Build collision'),
        Stage('shell', shell.run, inputs=shell.inputs, outputs=shell.outputs, title='Create the level'),
        Stage('shaderdb', shaderdb.run, deps=('shell', 'textures'), inputs=shaderdb.inputs, outputs=shaderdb.outputs,
              title='Shader database'),
        Stage('import', importer.run_import, deps=('split', 'textures', 'collision', 'shaderdb'), inputs=importer.inputs,
              outputs=importer.outputs, title='Import pieces'),
        Stage('place', importer.run_place, deps=('import',), inputs=importer.place_inputs, outputs=importer.place_outputs,
              title='Place pieces'),
        Stage('doors', doors.run, deps=('place', 'textures'), inputs=doors.inputs, outputs=doors.outputs, title='Doors'),
        Stage('lights', lights.run, deps=('doors',), inputs=lights.inputs, outputs=lights.outputs, title='Lights'),
        Stage('look', look.run, deps=('lights',), inputs=look.inputs, outputs=look.outputs, title='Sky, fog, exposure'),
        Stage('navmesh', nav.run, deps=('look',), inputs=nav.inputs, outputs=nav.outputs, title='Navmesh'),
        Stage('spawns', spawns.run, deps=('navmesh',), inputs=spawns.inputs, outputs=spawns.outputs, title='Spawns'),
        Stage('kill', kill.run, deps=('spawns',), inputs=kill.inputs, outputs=kill.outputs, title='Kill volumes'),
    ]
    if with_bake:
        st.append(Stage('bake', bake.run, deps=('kill',), inputs=bake.inputs, outputs=bake.outputs, title='Bake lighting'))
    last = 'bake' if with_bake else 'kill'
    if with_supremacy:           # the second level is made from the finished first one (bake included)
        st.append(Stage('supremacy', supremacy.run, deps=(last,), inputs=supremacy.inputs, outputs=supremacy.outputs, title='Supremacy level'))
        last = 'supremacy'
    st += [
        Stage('audit', finish.run_audit, deps=(last,), inputs=finish.inputs, outputs=finish.outputs, title='Checks'),
        Stage('mod', finish.run_mod, deps=('audit',), inputs=finish.mod_inputs, outputs=finish.mod_outputs, title='Build the mod'),
    ]
    return st

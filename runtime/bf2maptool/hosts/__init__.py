"""Host shells: everything that depends on the shipped level a new map is built on lives in one module per host."""
import importlib

from ..runner import StageError

HOSTS = ('kamino_01',)


def get(name):
    if name not in HOSTS:
        raise StageError('Unknown host shell %r. Available: %s' % (name, ', '.join(HOSTS)))
    return importlib.import_module('.' + name, __name__)

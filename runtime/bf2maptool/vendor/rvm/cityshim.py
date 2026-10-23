"""CityHash64WithSeed for the shader-database tools: the compiled `cityhash` package when it is installed (fast),
else the pure-Python transcription next to this file (same results, slower)."""
try:
    import cityhash                                  # noqa: F401  (pip package)
except ImportError:
    import cityhash64 as _pure

    class cityhash:                                  # noqa: N801  (same attribute the package exposes)
        @staticmethod
        def CityHash64WithSeed(data, seed):          # noqa: N802
            return _pure.city_hash_64_with_seed(bytes(data), seed)

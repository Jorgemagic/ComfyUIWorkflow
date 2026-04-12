import os


os.environ["TQDM_DISABLE"] = "1"


def _disable_tqdm_class(tqdm_class):
    original_init = getattr(tqdm_class, "__init__", None)
    if original_init is None or getattr(original_init, "_comfystream_patched", False):
        return

    def patched_init(self, *args, **kwargs):
        kwargs["disable"] = True
        return original_init(self, *args, **kwargs)

    patched_init._comfystream_patched = True
    tqdm_class.__init__ = patched_init


try:
    import tqdm.std

    _disable_tqdm_class(tqdm.std.tqdm)
except Exception:
    pass

try:
    import tqdm.asyncio

    _disable_tqdm_class(tqdm.asyncio.tqdm_asyncio)
except Exception:
    pass

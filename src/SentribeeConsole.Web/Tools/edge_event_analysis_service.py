#!/usr/bin/env python3
import base64
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Any
from urllib.request import Request, urlopen

from fastapi import FastAPI, Header, HTTPException
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel


BASE_DIR = Path(__file__).resolve().parent
ANALYZER = BASE_DIR / "console_event_analyzer.py"
OUTPUT_ROOT = Path(os.getenv("EDGE_EVENT_ANALYSIS_OUTPUT_ROOT", "/opt/sentribee-edge-analysis/artifacts"))
PUBLIC_BASE_URL = os.getenv("EDGE_EVENT_ANALYSIS_PUBLIC_BASE_URL", "http://ins1.sentribee.ai:8097/artifacts")
API_KEY = os.getenv("EDGE_EVENT_ANALYSIS_API_KEY", "")
PYTHON_BIN = os.getenv("EDGE_EVENT_ANALYSIS_PYTHON", sys.executable)
TIMEOUT_SECONDS = int(os.getenv("EDGE_EVENT_ANALYSIS_TIMEOUT_SECONDS", "180"))
USE_OPENAI = os.getenv("EDGE_EVENT_ANALYSIS_USE_OPENAI", "false").lower() in {"1", "true", "yes", "on"}
PERSON_MODEL = os.getenv("PERSON_MODEL", "/opt/sentribee-edge-ai/yolo11n.pt")
PPE_MODELS = [
    item.strip()
    for item in os.getenv("PPE_MODELS", f"{os.getenv('PPE_MODEL', '/opt/sentribee-edge-ai/best.pt')},{os.getenv('PPE_MODEL_2', '/opt/sentribee-edge-ai/best2.pt')}").split(",")
    if item.strip()
]


class AnalyzeRequest(BaseModel):
    eventId: int
    projectId: int
    deviceCode: str
    imageUrl: str | None = None
    imageBase64: str | None = None
    imageContentType: str | None = "image/jpeg"
    detectionJson: dict[str, Any] | list[Any] | None = None
    requiredPpe: list[str] | None = None
    personConfidence: float | None = 0.35
    ppeConfidence: float | None = 0.20
    sceneObjectConfidence: float | None = 0.25
    validationRatio: float | None = 0.20
    personCropScale: float | None = 1.10
    personCropMinSide: int | None = 96
    riskZones: list[Any] | None = None


app = FastAPI(title="Sentribee Edge Event Analysis", version="1.0.0")
OUTPUT_ROOT.mkdir(parents=True, exist_ok=True)
app.mount("/artifacts", StaticFiles(directory=str(OUTPUT_ROOT)), name="artifacts")


@app.get("/health")
def health() -> dict[str, Any]:
    return {
        "status": "ok",
        "analyzer": str(ANALYZER),
        "outputRoot": str(OUTPUT_ROOT),
        "personModel": PERSON_MODEL,
        "ppeModels": PPE_MODELS,
        "useOpenAI": USE_OPENAI,
    }


@app.post("/api/edge-event-analysis/analyze")
def analyze(payload: AnalyzeRequest, authorization: str | None = Header(default=None)) -> dict[str, Any]:
    require_auth(authorization)
    if not ANALYZER.exists():
        raise HTTPException(status_code=500, detail=f"Analyzer not found at {ANALYZER}")

    event_dir = reset_event_dir(OUTPUT_ROOT / "events" / str(payload.eventId))
    image_path = event_dir / f"source{image_extension(payload.imageContentType, payload.imageUrl)}"
    write_image(payload, image_path)

    detection_path = event_dir / "edge_payload_detection.json"
    if payload.detectionJson is not None:
        detection_path.write_text(json.dumps(payload.detectionJson, ensure_ascii=False), encoding="utf-8")

    output_json = event_dir / "analysis_result.json"
    public_prefix = f"{PUBLIC_BASE_URL.rstrip('/')}/events/{payload.eventId}"
    args = [
        PYTHON_BIN,
        str(ANALYZER),
        "--event-id",
        str(payload.eventId),
        "--project-id",
        str(payload.projectId),
        "--device-code",
        payload.deviceCode,
        "--image-path",
        str(image_path),
        "--image-url",
        payload.imageUrl or "",
        "--output-dir",
        str(event_dir),
        "--public-url-prefix",
        public_prefix,
        "--output-json",
        str(output_json),
        "--required-ppe",
        ",".join(payload.requiredPpe or ["helmet", "vest"]),
        "--person-conf",
        str(payload.personConfidence or 0.35),
        "--ppe-conf",
        str(payload.ppeConfidence or 0.20),
        "--scene-object-conf",
        str(payload.sceneObjectConfidence or 0.25),
        "--val-ratio",
        str(payload.validationRatio or 0.20),
        "--person-crop-scale",
        str(payload.personCropScale or 1.10),
        "--person-crop-min-side",
        str(payload.personCropMinSide or 96),
        "--person-model",
        PERSON_MODEL,
    ]
    for model in PPE_MODELS:
        args.extend(["--ppe-model", model])
    if payload.detectionJson is not None:
        args.extend(["--detection-json", str(detection_path)])
    if payload.riskZones:
        args.extend(["--risk-zones-json", json.dumps(payload.riskZones, ensure_ascii=False)])
    if USE_OPENAI and os.getenv("OPENAI_API_KEY"):
        args.append("--use-openai")
        if os.getenv("OPENAI_MODEL"):
            args.extend(["--openai-model", os.getenv("OPENAI_MODEL", "")])

    try:
        completed = subprocess.run(
            args,
            cwd=str(BASE_DIR),
            capture_output=True,
            text=True,
            timeout=TIMEOUT_SECONDS,
            check=False,
        )
    except subprocess.TimeoutExpired as exc:
        raise HTTPException(status_code=504, detail=f"Analysis timed out after {TIMEOUT_SECONDS}s: {exc}") from exc

    if completed.returncode != 0:
        raise HTTPException(
            status_code=500,
            detail={
                "message": "Analysis failed.",
                "stdout": trim_log(completed.stdout),
                "stderr": trim_log(completed.stderr),
            },
        )
    if not output_json.exists():
        raise HTTPException(status_code=500, detail="Analysis completed but no output JSON was created.")

    return json.loads(output_json.read_text(encoding="utf-8"))


def reset_event_dir(event_dir: Path) -> Path:
    event_dir.mkdir(parents=True, exist_ok=True)
    for child in event_dir.iterdir():
        if child.is_dir():
            shutil.rmtree(child)
        else:
            child.unlink()
    return event_dir


def require_auth(authorization: str | None) -> None:
    if not API_KEY:
        raise HTTPException(status_code=500, detail="EDGE_EVENT_ANALYSIS_API_KEY is not configured.")
    expected = f"Bearer {API_KEY}"
    if authorization != expected:
        raise HTTPException(status_code=401, detail="Unauthorized")


def write_image(payload: AnalyzeRequest, image_path: Path) -> None:
    if payload.imageBase64:
        image_path.write_bytes(base64.b64decode(payload.imageBase64))
        return
    if payload.imageUrl:
        request = Request(payload.imageUrl, headers={"User-Agent": "SentribeeEdgeAnalysis/1.0"})
        with urlopen(request, timeout=30) as response:
            image_path.write_bytes(response.read())
        return
    raise HTTPException(status_code=400, detail="imageUrl or imageBase64 is required.")


def image_extension(content_type: str | None, image_url: str | None) -> str:
    if content_type and "png" in content_type.lower():
        return ".png"
    if content_type and "webp" in content_type.lower():
        return ".webp"
    if image_url:
        suffix = Path(image_url.split("?", 1)[0]).suffix.lower()
        if suffix in {".jpg", ".jpeg", ".png", ".webp"}:
            return suffix
    return ".jpg"


def trim_log(value: str, limit: int = 2000) -> str:
    return value if len(value) <= limit else value[-limit:]

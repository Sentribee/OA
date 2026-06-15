#!/usr/bin/env python3
import json
import os
import subprocess
import sys
from collections import defaultdict
from datetime import datetime
from io import BytesIO
from urllib.parse import unquote, urlparse

import boto3
import requests
from PIL import Image, ImageOps


RISK_STATUSES = {"Severe Danger", "Ordinary Risk", "Real Risk"}
HASH_DISTANCE_THRESHOLD = int(os.environ.get("RISK_PERSON_HASH_DISTANCE", "14"))


def parse_conn(cs):
    parts = {}
    for item in cs.split(";"):
        if "=" in item:
            key, value = item.split("=", 1)
            parts[key.strip().lower()] = value.strip()
    return {
        "host": parts.get("server") or parts.get("host") or "localhost",
        "user": parts.get("user") or parts.get("uid") or parts.get("username"),
        "password": parts.get("password") or parts.get("pwd") or "",
        "database": parts.get("database") or parts.get("db"),
    }


conn = parse_conn(os.environ["ConnectionStrings__DefaultConnection"])
mysql_env = os.environ.copy()
mysql_env["MYSQL_PWD"] = conn["password"]
mysql_base = [
    "mysql",
    "-h",
    conn["host"],
    "-u",
    conn["user"],
    conn["database"],
    "--batch",
    "--raw",
    "--skip-column-names",
]


def mysql_select(sql):
    result = subprocess.run(mysql_base + ["-e", sql], env=mysql_env, text=True, capture_output=True, check=True)
    return result.stdout


def mysql_exec(sql):
    result = subprocess.run(mysql_base, env=mysql_env, input=sql, text=True, capture_output=True)
    if result.returncode != 0:
        raise RuntimeError(result.stderr.strip() or "mysql failed")


def sql_lit(value):
    if value is None or value == "":
        return "NULL"
    return "CONVERT(0x" + str(value).encode("utf-8").hex() + " USING utf8mb4)"


def sql_json(value):
    if value is None:
        return "NULL"
    return sql_lit(json.dumps(value, ensure_ascii=False, separators=(",", ":")))


def sql_num(value):
    return "NULL" if value is None else str(value)


def sql_date(value):
    return sql_lit(value)


def safe_int(value, default=0):
    try:
        if value in (None, "", "NULL"):
            return default
        return int(float(value))
    except Exception:
        return default


def safe_decimal(value):
    if value in (None, "", "NULL"):
        return None
    try:
        return float(value)
    except Exception:
        return None


def load_json(value):
    if not value or value == "NULL":
        return None
    try:
        return json.loads(value)
    except Exception:
        return None


def extract_int(detail, *keys):
    if not isinstance(detail, dict):
        return 0
    for key in keys:
        value = detail.get(key)
        if isinstance(value, (int, float)):
            return int(value)
        if isinstance(value, str) and value.strip():
            return safe_int(value)
    return 0


s3_bucket = os.environ.get("S3Storage__Bucket", "")
s3_region = os.environ.get("S3Storage__Region", "")
s3 = boto3.client(
    "s3",
    region_name=s3_region,
    aws_access_key_id=os.environ.get("S3Storage__AccessKeyId"),
    aws_secret_access_key=os.environ.get("S3Storage__SecretAccessKey"),
)


def read_image_bytes(url):
    if not url:
        return None
    parsed = urlparse(url)
    if s3_bucket and ".s3." in parsed.netloc and "amazonaws.com" in parsed.netloc:
        key = unquote(parsed.path.lstrip("/"))
        return s3.get_object(Bucket=s3_bucket, Key=key)["Body"].read()
    response = requests.get(url, timeout=30)
    response.raise_for_status()
    return response.content


def image_hash(url):
    data = read_image_bytes(url)
    if not data:
        return None
    with Image.open(BytesIO(data)) as image:
        gray = ImageOps.grayscale(image).resize((9, 8), Image.Resampling.LANCZOS)
        pixels = list(gray.getdata())
    bits = 0
    for row in range(8):
        for col in range(8):
            left = pixels[row * 9 + col]
            right = pixels[row * 9 + col + 1]
            bits = (bits << 1) | (1 if left > right else 0)
    return f"{bits:016x}"


def hamming(first, second):
    return (int(first, 16) ^ int(second, 16)).bit_count()


def query_subjects():
    sql = """
SELECT subject.id, subject.EdgeEventId, evt.EdgeDeviceId, device.ProjectId,
       DATE_FORMAT(evt.EventTimeUtc, '%Y-%m-%d') AS StatDate,
       evt.EventTimeUtc, subject.SubjectKey, subject.TrackingLabel,
       subject.CropImageUrl, subject.PreviewImageUrl
FROM bee_EdgeEventSubject AS subject
INNER JOIN bee_EdgeEvent AS evt ON evt.id = subject.EdgeEventId
INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
WHERE subject.SubjectType = 'Person'
  AND subject.IsRisk = 1
  AND evt.Status IN ('Severe Danger', 'Ordinary Risk', 'Real Risk')
ORDER BY device.ProjectId, evt.EdgeDeviceId, StatDate, evt.EventTimeUtc, evt.id, subject.id;
"""
    rows = []
    for line in mysql_select(sql).splitlines():
        parts = line.split("\t")
        if len(parts) < 10:
            continue
        rows.append(
            {
                "subject_id": safe_int(parts[0]),
                "event_id": safe_int(parts[1]),
                "edge_device_id": safe_int(parts[2]),
                "project_id": safe_int(parts[3]),
                "stat_date": parts[4],
                "event_time": parts[5],
                "subject_key": parts[6],
                "tracking_label": None if parts[7] == "NULL" else parts[7],
                "crop_url": None if parts[8] == "NULL" else parts[8],
                "preview_url": None if parts[9] == "NULL" else parts[9],
            }
        )
    return rows


def build_risk_person_groups(subjects):
    grouped = defaultdict(list)
    for subject in subjects:
        grouped[(subject["project_id"], subject["edge_device_id"], subject["stat_date"])].append(subject)

    all_groups = []
    failures = 0
    for (project_id, edge_device_id, stat_date), items in grouped.items():
        groups = []
        for item in items:
            try:
                item["similarity_hash"] = image_hash(item["crop_url"])
            except Exception as exc:
                failures += 1
                print(f"HASH_FAIL subject={item['subject_id']} event={item['event_id']} error={exc}", flush=True)
                item["similarity_hash"] = None

            matched = None
            item_subject_key = (item.get("subject_key") or "").strip().lower()
            if item["similarity_hash"]:
                for group in groups:
                    if group["similarity_hash"] and hamming(item["similarity_hash"], group["similarity_hash"]) <= HASH_DISTANCE_THRESHOLD:
                        matched = group
                        break
            if matched is None and item_subject_key:
                for group in groups:
                    if item_subject_key in group["subject_keys"]:
                        matched = group
                        break
            if matched is None:
                matched = {
                    "project_id": project_id,
                    "edge_device_id": edge_device_id,
                    "stat_date": stat_date,
                    "items": [],
                    "subject_keys": set(),
                    "similarity_hash": item["similarity_hash"],
                }
                groups.append(matched)
            matched["items"].append(item)
            if item_subject_key:
                matched["subject_keys"].add(item_subject_key)

        groups.sort(key=lambda group: (-len({item["event_id"] for item in group["items"]}), group["items"][0]["subject_id"]))
        for index, group in enumerate(groups, 1):
            items = group["items"]
            representative = items[0]
            event_ids = sorted({item["event_id"] for item in items})
            subject_ids = [item["subject_id"] for item in items]
            all_groups.append(
                {
                    "project_id": project_id,
                    "edge_device_id": edge_device_id,
                    "stat_date": stat_date,
                    "person_group_key": f"risk-person-{index:03}",
                    "display_label": representative["tracking_label"] or f"Risk Person {index}",
                    "representative_subject_id": representative["subject_id"],
                    "representative_crop_url": representative["crop_url"],
                    "representative_preview_url": representative["preview_url"],
                    "risk_event_count": len(event_ids),
                    "risk_subject_count": len(subject_ids),
                    "similarity_hash": group["similarity_hash"],
                    "subject_ids": subject_ids,
                    "event_ids": event_ids,
                    "first_event_at": min(item["event_time"] for item in items),
                    "last_event_at": max(item["event_time"] for item in items),
                }
            )
    print(f"RISK_PERSON_GROUPS groups={len(all_groups)} hashFailures={failures}", flush=True)
    return all_groups


def persist_risk_person_groups(groups):
    mysql_exec("DELETE FROM bee_EdgeDeviceDailyRiskPerson;\n")
    if not groups:
        return
    values = []
    for group in groups:
        values.append(
            "(%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s)" % (
                group["project_id"],
                group["edge_device_id"],
                sql_date(group["stat_date"]),
                sql_lit(group["person_group_key"]),
                sql_lit(group["display_label"]),
                sql_num(group["representative_subject_id"]),
                sql_lit(group["representative_crop_url"]),
                sql_lit(group["representative_preview_url"]),
                group["risk_event_count"],
                group["risk_subject_count"],
                sql_lit(group["similarity_hash"]),
                sql_json(group["subject_ids"]),
                sql_json(group["event_ids"]),
                sql_lit(group["first_event_at"]),
                sql_lit(group["last_event_at"]),
            )
        )
    for start in range(0, len(values), 200):
        chunk = values[start : start + 200]
        mysql_exec(
            """INSERT INTO bee_EdgeDeviceDailyRiskPerson
(ProjectId,EdgeDeviceId,StatDate,PersonGroupKey,DisplayLabel,RepresentativeSubjectId,RepresentativeCropImageUrl,RepresentativePreviewImageUrl,
 RiskEventCount,RiskSubjectCount,SimilarityHash,SubjectIdsJson,EventIdsJson,FirstEventAtUtc,LastEventAtUtc)
VALUES
"""
            + ",\n".join(chunk)
            + ";\n"
        )


def query_day_keys():
    sql = """
SELECT ProjectId, EdgeDeviceId, StatDate
FROM (
  SELECT MAX(ProjectId) AS ProjectId, EdgeDeviceId, StatDate
  FROM (
    SELECT device.ProjectId AS ProjectId, evt.EdgeDeviceId AS EdgeDeviceId, DATE(evt.EventTimeUtc) AS StatDate
    FROM bee_EdgeEvent AS evt
    INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
    UNION ALL
    SELECT ProjectId, EdgeDeviceId, DATE(ReportedAtUtc) AS StatDate
    FROM bee_EdgeAiHeartbeat
  ) AS rawDays
  GROUP BY EdgeDeviceId, StatDate
  UNION
  SELECT ProjectId, id AS EdgeDeviceId, CURDATE() AS StatDate
  FROM bee_EdgeDevice
) AS days
ORDER BY ProjectId, EdgeDeviceId, StatDate;
"""
    keys = []
    for line in mysql_select(sql).splitlines():
        project_id, edge_device_id, stat_date = line.split("\t")
        keys.append((safe_int(project_id), safe_int(edge_device_id), stat_date))
    return keys


def scalar_row(sql):
    output = mysql_select(sql).strip()
    if not output:
        return []
    return output.split("\t")


def rebuild_daily_stats():
    keys = query_day_keys()
    print(f"DAILY_KEYS total={len(keys)}", flush=True)
    mysql_exec("DELETE FROM bee_EdgeDeviceDailyStat;\n")
    for index, (project_id, edge_device_id, stat_date) in enumerate(keys, 1):
        heartbeat = scalar_row(
            f"""
SELECT DetailJson, ReportedAtUtc
FROM bee_EdgeAiHeartbeat
WHERE EdgeDeviceId = {edge_device_id}
  AND ReportedAtUtc >= {sql_lit(stat_date)}
  AND ReportedAtUtc < DATE_ADD({sql_lit(stat_date)}, INTERVAL 1 DAY)
ORDER BY ReportedAtUtc DESC, id DESC
LIMIT 1;
"""
        )
        detail = load_json(heartbeat[0]) if heartbeat else None
        last_heartbeat = heartbeat[1] if heartbeat and heartbeat[1] != "NULL" else None
        people = extract_int(detail, "peopleCount", "personCount", "currentPeopleCount", "workerCount", "recognizableWorkerCount")
        bracelets = extract_int(detail, "braceletCount", "bluetoothBraceletCount", "wristbandCount", "bleBraceletCount")
        machinery = extract_int(detail, "machineryVehicleCount", "vehicleCount", "heavyEquipmentCount", "plantCount")

        event_stats = scalar_row(
            f"""
SELECT COUNT(*), COALESCE(SUM(COALESCE(analysis.RiskPersonCount,0)),0), MAX(evt.EventTimeUtc)
FROM bee_EdgeEvent AS evt
LEFT JOIN bee_EdgeEventAnalysis AS analysis ON analysis.EdgeEventId = evt.id
WHERE evt.EdgeDeviceId = {edge_device_id}
  AND evt.EventTimeUtc >= {sql_lit(stat_date)}
  AND evt.EventTimeUtc < DATE_ADD({sql_lit(stat_date)}, INTERVAL 1 DAY)
  AND evt.Status IN ('Severe Danger','Ordinary Risk','Real Risk');
"""
        )
        risk_events = safe_int(event_stats[0]) if event_stats else 0
        risk_people = safe_int(event_stats[1]) if len(event_stats) > 1 else 0
        last_event = event_stats[2] if len(event_stats) > 2 and event_stats[2] != "NULL" else None

        latest_analysis = scalar_row(
            f"""
SELECT PeopleCount, MachineryVehicleCount, PpeComplianceRate
FROM bee_EdgeEvent AS evt
INNER JOIN bee_EdgeEventAnalysis AS analysis ON analysis.EdgeEventId = evt.id
WHERE evt.EdgeDeviceId = {edge_device_id}
  AND evt.EventTimeUtc >= {sql_lit(stat_date)}
  AND evt.EventTimeUtc < DATE_ADD({sql_lit(stat_date)}, INTERVAL 1 DAY)
  AND analysis.PpeComplianceRate IS NOT NULL
ORDER BY evt.EventTimeUtc DESC, evt.id DESC
LIMIT 1;
"""
        )
        if people == 0 and latest_analysis:
            people = safe_int(latest_analysis[0])
        if machinery == 0 and len(latest_analysis) > 1:
            machinery = safe_int(latest_analysis[1])
        ppe_rate = safe_decimal(latest_analysis[2]) if len(latest_analysis) > 2 else None

        top_group = scalar_row(
            f"""
SELECT PersonGroupKey, RiskEventCount
FROM bee_EdgeDeviceDailyRiskPerson
WHERE EdgeDeviceId = {edge_device_id}
  AND StatDate = {sql_lit(stat_date)}
ORDER BY RiskEventCount DESC, RiskSubjectCount DESC, PersonGroupKey
LIMIT 1;
"""
        )
        top_key = top_group[0] if top_group else None
        top_count = safe_int(top_group[1]) if len(top_group) > 1 else 0

        mysql_exec(
            f"""
INSERT INTO bee_EdgeDeviceDailyStat
    (ProjectId, EdgeDeviceId, StatDate, PeopleCount, BraceletCount, MachineryVehicleCount,
     PpeComplianceRate, RiskEventCount, RiskPersonCount, TopRiskSubjectKey, TopRiskSubjectRiskCount,
     LastHeartbeatAtUtc, LastEventAtUtc, DetailJson)
VALUES
    ({project_id}, {edge_device_id}, {sql_lit(stat_date)}, {people}, {bracelets}, {machinery},
     {sql_num(ppe_rate)}, {risk_events}, {risk_people}, {sql_lit(top_key)}, {top_count},
     {sql_lit(last_heartbeat)}, {sql_lit(last_event)}, {sql_json(detail)});
"""
        )
        if index % 25 == 0 or index == len(keys):
            print(f"DAILY_DONE {index}/{len(keys)}", flush=True)


def main():
    started = datetime.utcnow()
    print(f"START rebuild_edge_device_daily_stats threshold={HASH_DISTANCE_THRESHOLD}", flush=True)
    subjects = query_subjects()
    print(f"RISK_SUBJECTS total={len(subjects)}", flush=True)
    groups = build_risk_person_groups(subjects)
    persist_risk_person_groups(groups)
    rebuild_daily_stats()
    elapsed = (datetime.utcnow() - started).total_seconds()
    print(f"SUMMARY subjects={len(subjects)} groups={len(groups)} elapsed={elapsed:.1f}s", flush=True)


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(f"ERROR {exc}", file=sys.stderr, flush=True)
        raise

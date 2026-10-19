using System;
using System.Collections;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>A short, cosmetic presentation of the server's toss. The protected Spark is
    /// already in the snapshot; only its view is withheld until this coin reaches the hand.</summary>
    internal sealed class SparkCoinOpening : MonoBehaviour
    {
        private Transform _coin;
        private CardPresenter _card;
        private Transform _destination;
        private Material _brass, _face;
        private Mesh _disc;
        private Vector3 _landing;
        private bool _local;

        public static SparkCoinOpening Create(Transform parent, BoardController board,
            BoardAnchors anchors, ICardDatabase database, IArtProvider art,
            MatchOpeningDeal deal, PlayerId local)
        {
            if (!database.TryGet(MatchOpeningDeal.SparkCardId, out CardDefinition spark)) return null;
            Transform destination = anchors.AnchorFor(deal.SecondPlayer, Zone.Hand, deal.HandSlot);
            if (destination == null) return null;
            var host = new GameObject("The Spark opening toss");
            host.transform.SetParent(parent, false);
            var opening = host.AddComponent<SparkCoinOpening>();
            opening._destination = destination;
            opening._local = deal.SecondPlayer == local;
            opening._landing = new Vector3(0f, opening._local ? -2.65f : 2.65f, -0.30f);
            opening.BuildCoin();
            opening._card = board.BuildFocusedCard(host.transform, spark, new CardLiveState(default));
            opening._card.gameObject.SetActive(false);
            art?.LoadArt(spark.ArtId, sprite =>
            {
                if (opening == null || sprite == null) return;
                opening._card.SetArt(sprite);
                opening.PaintCoin(sprite);
            });
            return opening;
        }

        public IEnumerator Play(Func<bool> stillOpening)
        {
            _coin.gameObject.SetActive(true);
            const float toss = 0.95f, settle = 0.22f, hold = 0.28f, slide = 0.68f;
            float duration = toss + settle + hold + slide;
            Quaternion rest = Quaternion.Euler(_local ? 12f : -12f, 0f, _local ? -8f : 8f);
            for (float elapsed = 0f; elapsed < duration; elapsed += Time.unscaledDeltaTime)
            {
                if (!stillOpening() || _destination == null) yield break;
                if (elapsed < toss)
                {
                    float t = elapsed / toss;
                    _coin.position = Vector3.Lerp(new Vector3(0f, 0f, -0.3f), _landing, t)
                        + Vector3.back * (Mathf.Sin(t * Mathf.PI) * 2.6f);
                    _coin.rotation = rest * Quaternion.Euler(1440f * (1f - t), 0f, 24f * Mathf.Sin(t * Mathf.PI));
                }
                else if (elapsed < toss + settle)
                {
                    float t = (elapsed - toss) / settle;
                    _coin.position = _landing + Vector3.back * (Mathf.Sin(t * Mathf.PI) * 0.15f);
                    _coin.rotation = rest * Quaternion.Euler(0f, Mathf.Sin(t * Mathf.PI * 3f) * (1f - t) * 15f, 0f);
                }
                else if (elapsed < toss + settle + hold)
                {
                    _coin.position = _landing;
                    _coin.rotation = rest;
                }
                else
                {
                    float t = Mathf.Clamp01((elapsed - toss - settle - hold) / slide);
                    float e = Mathf.SmoothStep(0f, 1f, t);
                    Vector3 position = Vector3.Lerp(_landing, _destination.position, e)
                        + Vector3.back * (Mathf.Sin(t * Mathf.PI) * 0.4f);
                    _coin.position = position;
                    _coin.localScale = Vector3.one * Mathf.Max(0.001f, 1f - t * 3f);
                    _coin.gameObject.SetActive(t < 0.34f);
                    _card.gameObject.SetActive(true);
                    _card.transform.SetPositionAndRotation(position,
                        Quaternion.Slerp(rest, _destination.rotation, e));
                    _card.transform.localScale = Vector3.one * Mathf.Lerp(0.05f, HandFanLayout.RestScale,
                        Mathf.Clamp01(t * 3f));
                    // The opponent receives a known Spark, but their resting hand remains backs.
                    if (!_local && t > 0.7f) _card.SetHidden();
                }
                yield return null;
            }
        }

        private void BuildCoin()
        {
            _brass = TabletopToyGeometry.Material("Ember coin edge", new Color(0.48f, 0.22f, 0.055f), 0.75f);
            _face = TabletopToyGeometry.Material("The Spark coin face", new Color(1f, 0.58f, 0.16f), 0.1f);
            _coin = new GameObject("Spinning ember coin").transform;
            _coin.SetParent(transform, false);
            GameObject edge = TabletopToyGeometry.Solid(_coin, "Milled bronze edge", PrimitiveType.Cylinder,
                Vector3.zero, new Vector3(1.4f, 0.065f, 1.4f), _brass);
            edge.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            _disc = Disc();
            for (int side = 0; side < 2; side++)
            {
                var face = new GameObject(side == 0 ? "Ember face" : "Ember reverse");
                face.transform.SetParent(_coin, false);
                face.transform.localPosition = new Vector3(0f, 0f, side == 0 ? -0.067f : 0.067f);
                face.transform.localRotation = Quaternion.Euler(0f, side == 0 ? 0f : 180f, 0f);
                face.AddComponent<MeshFilter>().sharedMesh = _disc;
                face.AddComponent<MeshRenderer>().sharedMaterial = _face;
            }
            _coin.gameObject.SetActive(false);
        }

        private void PaintCoin(Sprite sprite)
        {
            // The authored 512px image holds the coin between fingers. Crop the circular ember
            // face inside its rim, rather than printing the hand and background on the token.
            Rect rect = sprite.textureRect;
            _face.SetColor("_BaseColor", Color.white);
            _face.SetTexture("_BaseMap", sprite.texture);
            _face.SetTextureScale("_BaseMap", new Vector2(rect.width / sprite.texture.width,
                rect.height / sprite.texture.height));
            _face.SetTextureOffset("_BaseMap", new Vector2(rect.x / sprite.texture.width,
                rect.y / sprite.texture.height));
        }

        private static Mesh Disc()
        {
            const int sides = 64;
            var vertices = new Vector3[sides + 1];
            var uv = new Vector2[sides + 1];
            var triangles = new int[sides * 3];
            Vector2 center = new Vector2(0.525f, 0.513f);
            uv[0] = center;
            for (int i = 0; i < sides; i++)
            {
                float angle = i * Mathf.PI * 2f / sides;
                Vector2 direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                vertices[i + 1] = direction * 0.66f;
                uv[i + 1] = center + direction * 0.17f;
                triangles[i * 3] = 0;
                triangles[i * 3 + 1] = (i + 1) % sides + 1;
                triangles[i * 3 + 2] = i + 1;
            }
            var mesh = new Mesh { name = "Ember coin circular face", vertices = vertices, uv = uv, triangles = triangles };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private void OnDestroy()
        {
            TabletopToyGeometry.Destroy(_brass);
            TabletopToyGeometry.Destroy(_face);
            TabletopToyGeometry.Destroy(_disc);
        }
    }
}

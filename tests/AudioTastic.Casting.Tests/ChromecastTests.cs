using System.Buffers.Binary;
using System.Text.Json.Nodes;
using AudioTastic.Casting.Chromecast;

namespace AudioTastic.Casting.Tests;

/// <summary>CN-01: the Cast v2 message format and the media requests and answers.</summary>
public sealed class ChromecastTests
{
    [Fact]
    public void Cast_message_round_trips_and_matches_the_protobuf_layout()
    {
        var message = new CastMessage("sender-0", "receiver-0", "urn:x-cast:com.google.cast.tp.connection", """{"type":"CONNECT"}""");
        var bytes = message.Encode();

        // field 1 (varint 0), field 2 (length-delimited "sender-0")
        Assert.Equal(new byte[] { 0x08, 0x00, 0x12, 0x08 }, bytes[..4]);
        Assert.Equal(message, CastMessage.Decode(bytes));

        var frame = message.Frame();
        Assert.Equal(bytes.Length, BinaryPrimitives.ReadInt32BigEndian(frame));
        Assert.Equal(bytes, frame[4..]);
    }

    [Fact]
    public void Long_utf8_payloads_use_multi_byte_lengths()
    {
        var payload = "{\"title\":\"" + new string('æ', 300) + "\"}"; // 600+ bytes: a two-byte varint length
        var message = new CastMessage("a", "b", "c", payload);
        Assert.Equal(payload, CastMessage.Decode(message.Encode()).Payload);
    }

    [Fact]
    public void Unknown_and_binary_fields_are_skipped()
    {
        var known = new CastMessage("s", "d", "n", "{}").Encode().ToList();
        known.AddRange([0x3A, 0x02, 0xFF, 0xFE]); // field 7 (payload_binary), 2 bytes
        known.AddRange([0x4D, 1, 2, 3, 4]);       // field 9, fixed32
        Assert.Equal("{}", CastMessage.Decode(known.ToArray()).Payload);
        Assert.Throws<InvalidDataException>(() => CastMessage.Decode([0x12, 0x10, 0x41]));
    }

    [Fact]
    public void Load_request_carries_url_type_music_metadata_and_start()
    {
        var track = new CastTrack(new Uri("http://192.168.0.70:5000/media/abc/a.flac"), "audio/flac", "Mundo Imundo", "Demonizz", "Album",
            new Uri("http://192.168.0.70:5000/art/abc.jpg"), TimeSpan.FromSeconds(452));
        var load = ChromecastSession.LoadRequest(track, TimeSpan.FromSeconds(60));

        Assert.Equal("LOAD", load["type"]!.GetValue<string>());
        Assert.True(load["autoplay"]!.GetValue<bool>());
        Assert.Equal(60, load["currentTime"]!.GetValue<double>());
        var media = load["media"]!;
        Assert.Equal("http://192.168.0.70:5000/media/abc/a.flac", media["contentId"]!.GetValue<string>());
        Assert.Equal("audio/flac", media["contentType"]!.GetValue<string>());
        Assert.Equal("BUFFERED", media["streamType"]!.GetValue<string>());
        Assert.Equal(452, media["duration"]!.GetValue<double>());
        Assert.Equal(3, media["metadata"]!["metadataType"]!.GetValue<int>()); // music track
        Assert.Equal("Demonizz", media["metadata"]!["artist"]!.GetValue<string>());
        Assert.Equal("Album", media["metadata"]!["albumName"]!.GetValue<string>());
        Assert.Equal("http://192.168.0.70:5000/art/abc.jpg", media["metadata"]!["images"]![0]!["url"]!.GetValue<string>());
    }

    [Fact]
    public void Media_status_is_read_from_the_device_answer()
    {
        var payload = JsonNode.Parse("""
            {"type":"MEDIA_STATUS","requestId":4,"status":[{"mediaSessionId":1,"playbackRate":1,"playerState":"PLAYING",
             "currentTime":120.5,"media":{"duration":452.1},"volume":{"level":0.4,"muted":false}}]}
            """)!.AsObject();
        var status = ChromecastSession.ParseMediaStatus(payload);
        Assert.NotNull(status);
        Assert.Equal("PLAYING", status.PlayerState);
        Assert.Equal(TimeSpan.FromSeconds(120.5), status.CurrentTime);
        Assert.Equal(TimeSpan.FromSeconds(452.1), status.Duration);
        Assert.Equal(0.4, status.Volume);
        Assert.False(status.Muted);

        var finished = ChromecastSession.ParseMediaStatus(JsonNode.Parse("""{"type":"MEDIA_STATUS","status":[{"playerState":"IDLE","idleReason":"FINISHED"}]}""")!.AsObject());
        Assert.Equal("FINISHED", finished!.IdleReason);
        Assert.Null(ChromecastSession.ParseMediaStatus(JsonNode.Parse("""{"type":"MEDIA_STATUS","status":[]}""")!.AsObject()));
    }
}

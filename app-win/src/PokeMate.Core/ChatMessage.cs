namespace PokeMate.Core;

// Task 2 는 ClaudeCommand·ClaudeRunner 가 쓰는 ImageAttachment 만 옮겼다. ChatMessage 는 이 파일에 나중 작업이 채운다.

/// Claude 에게 보낼 이미지 한 장.
public sealed record ImageAttachment(string MediaType, byte[] Data, string Path)
{
    // MediaType: image/png, image/jpeg ... / Path: 화면 표시용 저장 위치

    public bool Equals(ImageAttachment? other) =>
        other is not null && MediaType == other.MediaType && Path == other.Path && Data.AsSpan().SequenceEqual(other.Data);

    public override int GetHashCode() => HashCode.Combine(MediaType, Path, Data.Length);
}

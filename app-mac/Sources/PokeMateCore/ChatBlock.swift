import Foundation

public enum ChatBlock {
    public static let tag = "pokemate"
    public static let legacyTag = "pocketmate"

    public static func pattern(_ name: String) -> String {
        "```(?:\(tag)|\(legacyTag))-\(name)[ \\t]*\\n(.*?)```"
    }
}
